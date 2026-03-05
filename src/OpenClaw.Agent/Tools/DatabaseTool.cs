using System.Data.Common;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Core.Security;

namespace OpenClaw.Agent.Tools;

/// <summary>
/// Native replica of the OpenClaw database plugin.
/// Executes SQL queries against SQLite, PostgreSQL, or MySQL databases.
/// Uses System.Data.Common (DbProviderFactory) for AOT-compatible database access.
/// Write operations (INSERT/UPDATE/DELETE/CREATE/DROP/ALTER) are gated behind AllowWrite.
/// </summary>
public sealed class DatabaseTool : ITool, IDisposable
{
    private readonly DatabaseConfig _config;
    private readonly ToolingConfig? _toolingConfig;
    private readonly ILogger? _logger;

    public DatabaseTool(DatabaseConfig config, ILogger? logger = null, ToolingConfig? toolingConfig = null)
    {
        _config = config;
        _toolingConfig = toolingConfig;
        _logger = logger;

        if (config.AllowWrite)
            _logger?.LogWarning("DatabaseTool: AllowWrite is enabled. " +
                "The LLM can execute arbitrary write operations. " +
                "Connect with a read-only database user for safety.");
    }

    public string Name => "database";
    public string Description =>
        "Execute SQL queries against a database. " +
        "Supports SQLite, PostgreSQL, and MySQL. " +
        "Use for data retrieval, schema inspection, and (if enabled) data modification.";
    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "description": "Action to perform",
              "enum": ["query", "execute", "schema", "tables"]
            },
            "sql": {
              "type": "string",
              "description": "SQL query or statement to execute"
            },
            "table": {
              "type": "string",
              "description": "Table name (for schema action)"
            }
          },
          "required": ["action"]
        }
        """;

    // Keywords that indicate mutating/admin SQL operations.
    private static readonly HashSet<string> WriteKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "DROP", "CREATE", "ALTER", "TRUNCATE", "MERGE", "REPLACE",
        "UPSERT", "VACUUM", "REINDEX", "ATTACH", "DETACH", "GRANT", "REVOKE", "SET", "CALL", "EXEC", "EXECUTE"
    };

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var args = JsonDocument.Parse(argumentsJson);
        var action = args.RootElement.GetProperty("action").GetString()!.ToLowerInvariant();

        if (_toolingConfig?.ReadOnlyMode == true && action == "execute")
            return "Error: database execute action is disabled because Tooling.ReadOnlyMode is enabled.";

        var connString = ResolveConnectionString();
        if (string.IsNullOrWhiteSpace(connString))
            return "Error: Database connection string not configured. Set Database.ConnectionString.";

        try
        {
            return action switch
            {
                "query" => await RunQueryAsync(args.RootElement, connString, ct),
                "execute" => await RunExecuteAsync(args.RootElement, connString, ct),
                "tables" => await ListTablesAsync(connString, ct),
                "schema" => await GetSchemaAsync(args.RootElement, connString, ct),
                _ => $"Error: Unsupported database action '{action}'. Use: query, execute, tables, schema."
            };
        }
        catch (DbException ex)
        {
            return $"Error: Database operation failed — {ex.Message}";
        }
        catch (InvalidOperationException ex)
        {
            return $"Error: Database configuration issue — {ex.Message}";
        }
    }

    private async Task<string> RunQueryAsync(JsonElement args, string connString, CancellationToken ct)
    {
        var sql = args.TryGetProperty("sql", out var s) ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(sql))
            return "Error: 'sql' is required for query action.";

        var policyError = ValidateSqlPolicy(sql);
        if (policyError is not null)
            return policyError;

        // Block write operations through query action
        if (IsWriteOperation(sql))
            return "Error: Write operations must use the 'execute' action, not 'query'.";

        await using var conn = await OpenConnectionAsync(connString, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = _config.TimeoutSeconds;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await FormatResultSetAsync(reader, ct);
    }

    private async Task<string> RunExecuteAsync(JsonElement args, string connString, CancellationToken ct)
    {
        var sql = args.TryGetProperty("sql", out var s) ? s.GetString() : null;
        if (string.IsNullOrWhiteSpace(sql))
            return "Error: 'sql' is required for execute action.";

        var policyError = ValidateSqlPolicy(sql);
        if (policyError is not null)
            return policyError;

        if (!_config.AllowWrite && IsWriteOperation(sql))
            return "Error: Write operations are disabled. Set Database.AllowWrite = true to enable.";

        await using var conn = await OpenConnectionAsync(connString, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = _config.TimeoutSeconds;

        var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);
        return $"Statement executed successfully. Rows affected: {rowsAffected}";
    }

    private async Task<string> ListTablesAsync(string connString, CancellationToken ct)
    {
        var sql = _config.Provider.ToLowerInvariant() switch
        {
            "sqlite" => "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name",
            "postgres" => "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' ORDER BY table_name",
            "mysql" => "SHOW TABLES",
            _ => "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' ORDER BY table_name"
        };

        await using var conn = await OpenConnectionAsync(connString, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = _config.TimeoutSeconds;

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("Tables:");
        var count = 0;
        while (await reader.ReadAsync(ct))
        {
            var table = reader.GetString(0);
            if (!IsTableAllowed(table))
                continue;

            count++;
            sb.AppendLine($"  {table}");
        }

        if (count == 0)
            sb.AppendLine("  (no tables found)");

        return sb.ToString();
    }

    private async Task<string> GetSchemaAsync(JsonElement args, string connString, CancellationToken ct)
    {
        var table = args.TryGetProperty("table", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(table))
            return "Error: 'table' is required for schema action.";

        if (!IsTableAllowed(table))
            return $"Error: Access denied for table '{table}'.";

        var provider = _config.Provider.ToLowerInvariant();

        // Validate table name BEFORE opening connection for providers that need it
        if (provider is "sqlite" or "mysql")
        {
            if (!IsValidIdentifier(table))
                return "Error: Invalid table name. Only alphanumeric characters, underscores, and dots are allowed.";
        }

        // Use parameterized queries to prevent SQL injection in schema lookups
        await using var conn = await OpenConnectionAsync(connString, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = _config.TimeoutSeconds;

        if (provider == "sqlite")
        {
            // PRAGMA does not support parameterized queries. IsValidIdentifier (above)
            // restricts to [a-zA-Z0-9_.-]; defense-in-depth: escape single quotes.
            var escapedTable = table.Replace("'", "''");
            cmd.CommandText = $"PRAGMA table_info('{escapedTable}')";
        }
        else if (provider == "mysql")
        {
            // DESCRIBE does not support parameterized queries. IsValidIdentifier (above)
            // restricts to [a-zA-Z0-9_.-]; defense-in-depth: escape backticks.
            var escapedTable = table.Replace("`", "``");
            cmd.CommandText = $"DESCRIBE `{escapedTable}`";
        }
        else
        {
            // PostgreSQL and others: use parameterized query via information_schema
            cmd.CommandText = "SELECT column_name, data_type, is_nullable, column_default " +
                              "FROM information_schema.columns " +
                              "WHERE table_name = @tableName " +
                              "ORDER BY ordinal_position";
            var param = cmd.CreateParameter();
            param.ParameterName = "@tableName";
            param.Value = table;
            cmd.Parameters.Add(param);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await FormatResultSetAsync(reader, ct, $"Schema for table: {table}");
    }

    private async Task<string> FormatResultSetAsync(DbDataReader reader, CancellationToken ct, string? header = null)
    {
        var sb = new StringBuilder();
        if (header is not null)
            sb.AppendLine(header);

        var fieldCount = reader.FieldCount;
        if (fieldCount == 0)
            return header ?? "(no columns)";

        // Column headers
        var columns = new string[fieldCount];
        var widths = new int[fieldCount];
        for (var i = 0; i < fieldCount; i++)
        {
            columns[i] = reader.GetName(i);
            widths[i] = columns[i].Length;
        }

        // Read all rows (up to limit)
        var rows = new List<string[]>();
        while (await reader.ReadAsync(ct) && rows.Count < _config.MaxRows)
        {
            var row = new string[fieldCount];
            for (var i = 0; i < fieldCount; i++)
            {
                row[i] = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i)?.ToString() ?? "NULL";
                widths[i] = Math.Max(widths[i], Math.Min(row[i].Length, 50));
            }
            rows.Add(row);
        }

        // Format as table
        var divider = new StringBuilder();
        for (var i = 0; i < fieldCount; i++)
        {
            if (i > 0) { sb.Append(" | "); divider.Append("-+-"); }
            sb.Append(columns[i].PadRight(widths[i]));
            divider.Append(new string('-', widths[i]));
        }
        sb.AppendLine();
        sb.AppendLine(divider.ToString());

        foreach (var row in rows)
        {
            for (var i = 0; i < fieldCount; i++)
            {
                if (i > 0) sb.Append(" | ");
                var val = row[i].Length > 50 ? row[i][..47] + "..." : row[i];
                sb.Append(val.PadRight(widths[i]));
            }
            sb.AppendLine();
        }

        sb.AppendLine($"\n({rows.Count} row{(rows.Count == 1 ? "" : "s")})");

        if (rows.Count == _config.MaxRows)
            sb.AppendLine($"(results limited to {_config.MaxRows} rows)");

        return sb.ToString();
    }

    private async Task<DbConnection> OpenConnectionAsync(string connString, CancellationToken ct)
    {
        var conn = CreateConnection(connString);
        await conn.OpenAsync(ct);
        return conn;
    }

    /// <summary>
    /// Create a DbConnection based on the configured provider.
    /// For SQLite, uses Microsoft.Data.Sqlite which is AOT-friendly.
    /// For PostgreSQL and MySQL, requires the appropriate NuGet package.
    /// Falls back to a factory lookup.
    /// </summary>
    private DbConnection CreateConnection(string connString)
    {
        // Use provider factory pattern for extensibility.
        // The actual provider assembly must be referenced at build time or registered.
        var providerName = _config.Provider.ToLowerInvariant() switch
        {
            "sqlite" => "Microsoft.Data.Sqlite",
            "postgres" => "Npgsql",
            "mysql" => "MySqlConnector",
            _ => _config.Provider
        };

        if (!DbProviderFactories.TryGetFactory(providerName, out var factory))
        {
            // Try common type names as a fallback
            throw new InvalidOperationException(
                $"Database provider '{_config.Provider}' is not registered. " +
                $"Install the appropriate NuGet package " +
                $"(e.g., Microsoft.Data.Sqlite for SQLite, Npgsql for PostgreSQL, MySqlConnector for MySQL) " +
                $"and register via DbProviderFactories.RegisterFactory().");
        }

        var conn = factory.CreateConnection()
            ?? throw new InvalidOperationException($"Failed to create connection for provider '{providerName}'.");
        conn.ConnectionString = connString;
        return conn;
    }

    private static bool IsWriteOperation(string sql)
    {
        foreach (var token in EnumerateSqlTokens(sql))
        {
            if (WriteKeywords.Contains(token))
                return true;
        }

        return false;
    }

    private string? ValidateSqlPolicy(string sql)
    {
        if (!_config.AllowMultiStatement && HasMultipleStatements(sql))
            return "Error: Multiple SQL statements are disabled. Set Database.AllowMultiStatement = true to enable.";

        if (_config.AllowedTables.Length == 0 && _config.DeniedTables.Length == 0)
            return null;

        var tableRefs = ExtractReferencedTables(sql);
        foreach (var table in tableRefs)
        {
            if (!IsTableAllowed(table))
                return $"Error: Access denied for table '{table}'.";
        }

        return null;
    }

    private bool IsTableAllowed(string tableName)
    {
        var normalized = NormalizeIdentifier(tableName);

        foreach (var denied in _config.DeniedTables)
        {
            if (IdentifiersEqual(normalized, denied))
                return false;
        }

        if (_config.AllowedTables.Length == 0)
            return true;

        foreach (var allowed in _config.AllowedTables)
        {
            if (IdentifiersEqual(normalized, allowed))
                return true;
        }

        return false;
    }

    private static bool IdentifiersEqual(string left, string right)
        => string.Equals(left, NormalizeIdentifier(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeIdentifier(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return trimmed;

        var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i].Trim();
            if ((p.StartsWith('"') && p.EndsWith('"')) ||
                (p.StartsWith('`') && p.EndsWith('`')) ||
                (p.StartsWith('[') && p.EndsWith(']')))
            {
                p = p[1..^1];
            }

            parts[i] = p;
        }

        return string.Join('.', parts);
    }

    private static bool HasMultipleStatements(string sql)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inBacktickQuote = false;
        var inBracketQuote = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (inLineComment)
            {
                if (c is '\n' or '\r')
                    inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                if (c == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                continue;
            }

            if (inSingleQuote)
            {
                if (c == '\'' && next == '\'')
                {
                    i++;
                    continue;
                }

                if (c == '\'')
                    inSingleQuote = false;

                continue;
            }

            if (inDoubleQuote)
            {
                if (c == '"' && next == '"')
                {
                    i++;
                    continue;
                }

                if (c == '"')
                    inDoubleQuote = false;

                continue;
            }

            if (inBacktickQuote)
            {
                if (c == '`')
                    inBacktickQuote = false;
                continue;
            }

            if (inBracketQuote)
            {
                if (c == ']')
                    inBracketQuote = false;
                continue;
            }

            if (c == '-' && next == '-')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (c == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            if (c == '\'')
            {
                inSingleQuote = true;
                continue;
            }

            if (c == '"')
            {
                inDoubleQuote = true;
                continue;
            }

            if (c == '`')
            {
                inBacktickQuote = true;
                continue;
            }

            if (c == '[')
            {
                inBracketQuote = true;
                continue;
            }

            if (c == ';')
            {
                for (var j = i + 1; j < sql.Length; j++)
                {
                    var tail = sql[j];
                    if (!char.IsWhiteSpace(tail))
                        return true;
                }
            }
        }

        return false;
    }

    private static IReadOnlySet<string> ExtractReferencedTables(string sql)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var cleaned = StripSqlLiteralsAndComments(sql);
        var parts = cleaned.Split(
            new[] { ' ', '\t', '\r', '\n', '(', ')', ',', ';' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = 0; i < parts.Length; i++)
        {
            if (!IsTableKeyword(parts[i]))
                continue;

            var candidate = ReadNextTableIdentifier(parts, i + 1);
            if (candidate.Length == 0)
                continue;

            if (string.Equals(candidate, "SELECT", StringComparison.OrdinalIgnoreCase))
                continue;

            candidate = NormalizeIdentifier(candidate);
            if (candidate.Length == 0)
                continue;

            refs.Add(candidate);
        }

        return refs;
    }

    private static string ReadNextTableIdentifier(string[] parts, int startIndex)
    {
        for (var i = startIndex; i < parts.Length; i++)
        {
            var token = parts[i].Trim();
            if (token.Length == 0)
                continue;

            if (token.Equals("ONLY", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("LATERAL", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("OUTER", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("INNER", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("LEFT", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("RIGHT", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("FULL", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("CROSS", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var raw = new StringBuilder(token);
            if (token.StartsWith('[') && !token.EndsWith(']'))
            {
                for (var j = i + 1; j < parts.Length; j++)
                {
                    raw.Append(' ').Append(parts[j]);
                    if (parts[j].EndsWith(']'))
                        break;
                }
            }

            return raw.ToString();
        }

        return string.Empty;
    }

    private static bool IsTableKeyword(string token)
        => token.Equals("FROM", StringComparison.OrdinalIgnoreCase)
           || token.Equals("JOIN", StringComparison.OrdinalIgnoreCase)
           || token.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
           || token.Equals("INTO", StringComparison.OrdinalIgnoreCase)
           || token.Equals("TABLE", StringComparison.OrdinalIgnoreCase)
           || token.Equals("DESCRIBE", StringComparison.OrdinalIgnoreCase);

    private static string StripSqlLiteralsAndComments(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inBacktickQuote = false;
        var inBracketQuote = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (inLineComment)
            {
                if (c is '\n' or '\r')
                {
                    inLineComment = false;
                    sb.Append(' ');
                }
                continue;
            }

            if (inBlockComment)
            {
                if (c == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                    sb.Append(' ');
                }
                continue;
            }

            if (inSingleQuote)
            {
                if (c == '\'' && next == '\'')
                {
                    i++;
                    continue;
                }

                if (c == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                if (c == '"' && next == '"')
                {
                    sb.Append('"');
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    inDoubleQuote = false;
                    sb.Append(c);
                    continue;
                }

                sb.Append(c);
                continue;
            }

            if (inBacktickQuote)
            {
                if (c == '`')
                {
                    inBacktickQuote = false;
                    sb.Append(c);
                    continue;
                }

                sb.Append(c);
                continue;
            }

            if (inBracketQuote)
            {
                if (c == ']')
                {
                    inBracketQuote = false;
                    sb.Append(c);
                    continue;
                }

                sb.Append(c);
                continue;
            }

            if (c == '-' && next == '-')
            {
                inLineComment = true;
                i++;
                continue;
            }

            if (c == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            if (c == '\'')
            {
                inSingleQuote = true;
                sb.Append(' ');
                continue;
            }

            if (c == '"')
            {
                inDoubleQuote = true;
                sb.Append(c);
                continue;
            }

            if (c == '`')
            {
                inBacktickQuote = true;
                sb.Append(c);
                continue;
            }

            if (c == '[')
            {
                inBracketQuote = true;
                sb.Append(c);
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static IEnumerable<string> EnumerateSqlTokens(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            yield break;

        var token = new StringBuilder(16);
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var inBacktickQuote = false;
        var inBracketQuote = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (inLineComment)
            {
                if (c is '\n' or '\r')
                    inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                if (c == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                continue;
            }

            if (inSingleQuote)
            {
                if (c == '\'' && next == '\'')
                {
                    i++; // Escaped quote in SQL string literal.
                    continue;
                }

                if (c == '\'')
                    inSingleQuote = false;

                continue;
            }

            if (inDoubleQuote)
            {
                if (c == '"' && next == '"')
                {
                    i++; // Escaped quote in quoted identifier.
                    continue;
                }

                if (c == '"')
                    inDoubleQuote = false;

                continue;
            }

            if (inBacktickQuote)
            {
                if (c == '`')
                    inBacktickQuote = false;
                continue;
            }

            if (inBracketQuote)
            {
                if (c == ']')
                    inBracketQuote = false;
                continue;
            }

            if (c == '-' && next == '-')
            {
                FlushTokenIfNeeded(token, out var completedToken);
                if (completedToken is not null)
                    yield return completedToken;
                inLineComment = true;
                i++;
                continue;
            }

            if (c == '/' && next == '*')
            {
                FlushTokenIfNeeded(token, out var completedToken);
                if (completedToken is not null)
                    yield return completedToken;
                inBlockComment = true;
                i++;
                continue;
            }

            if (c == '\'')
            {
                FlushTokenIfNeeded(token, out var completedToken);
                if (completedToken is not null)
                    yield return completedToken;
                inSingleQuote = true;
                continue;
            }

            if (c == '"')
            {
                FlushTokenIfNeeded(token, out var completedToken);
                if (completedToken is not null)
                    yield return completedToken;
                inDoubleQuote = true;
                continue;
            }

            if (c == '`')
            {
                FlushTokenIfNeeded(token, out var completedToken);
                if (completedToken is not null)
                    yield return completedToken;
                inBacktickQuote = true;
                continue;
            }

            if (c == '[')
            {
                FlushTokenIfNeeded(token, out var completedToken);
                if (completedToken is not null)
                    yield return completedToken;
                inBracketQuote = true;
                continue;
            }

            if (char.IsLetter(c) || (token.Length > 0 && char.IsDigit(c)))
            {
                token.Append(char.ToUpperInvariant(c));
                continue;
            }

            FlushTokenIfNeeded(token, out var tokenValue);
            if (tokenValue is not null)
                yield return tokenValue;
        }

        FlushTokenIfNeeded(token, out var trailing);
        if (trailing is not null)
            yield return trailing;
    }

    private static void FlushTokenIfNeeded(StringBuilder token, out string? value)
    {
        if (token.Length == 0)
        {
            value = null;
            return;
        }

        value = token.ToString();
        token.Clear();
    }

    private string? ResolveConnectionString() => SecretResolver.Resolve(_config.ConnectionString);

    /// <summary>
    /// Validates that a table name is a safe SQL identifier.
    /// Allows alphanumeric, underscores, dots (schema.table), and hyphens.
    /// </summary>
    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
            return false;

        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '.' && c != '-')
                return false;
        }
        return true;
    }

    public void Dispose() { }
}
