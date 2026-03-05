using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenClaw.Core.Memory;

internal static class MemoryRetentionArchive
{
    public static async ValueTask ArchivePayloadAsync(
        string archiveRoot,
        DateTimeOffset nowUtc,
        string kind,
        string id,
        DateTimeOffset expiresAtUtc,
        string sourceBackend,
        string payloadJson,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(archiveRoot))
            throw new ArgumentException("archiveRoot must be set.", nameof(archiveRoot));
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("kind must be set.", nameof(kind));
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("id must be set.", nameof(id));

        using var payload = JsonDocument.Parse(payloadJson);

        var now = nowUtc.UtcDateTime;
        var archiveBase = Path.GetFullPath(archiveRoot);
        var destinationDir = Path.Combine(
            archiveBase,
            now.ToString("yyyy"),
            now.ToString("MM"),
            now.ToString("dd"),
            kind);
        Directory.CreateDirectory(destinationDir);

        var fileName = $"{EncodeId(id)}-{now:yyyyMMddTHHmmssfffffffZ}.json";
        var destinationPath = Path.Combine(destinationDir, fileName);
        var tempPath = destinationPath + ".tmp";

        try
        {
            await using (var stream = new FileStream(tempPath, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            }))
            {
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                {
                    Indented = false
                });

                writer.WriteStartObject();
                writer.WriteString("kind", kind);
                writer.WriteString("id", id);
                writer.WriteString("sweptAtUtc", nowUtc.UtcDateTime);
                writer.WriteString("expiresAtUtc", expiresAtUtc.UtcDateTime);
                writer.WriteString("sourceBackend", sourceBackend);
                writer.WritePropertyName("payload");
                payload.RootElement.WriteTo(writer);
                writer.WriteEndObject();
                await writer.FlushAsync(ct);
                await stream.FlushAsync(ct);
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* ignore cleanup failures */ }
            throw;
        }
    }

    public static (int DeletedFiles, int Errors, List<string> ErrorMessages) PurgeExpiredArchives(
        string archiveRoot,
        DateTimeOffset nowUtc,
        int retentionDays,
        CancellationToken ct)
    {
        var deleted = 0;
        var errors = 0;
        var errorMessages = new List<string>(capacity: 4);

        if (string.IsNullOrWhiteSpace(archiveRoot) || !Directory.Exists(archiveRoot))
            return (deleted, errors, errorMessages);

        var cutoff = nowUtc.UtcDateTime.AddDays(-Math.Max(1, retentionDays));
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(archiveRoot, "*.json", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            errors++;
            errorMessages.Add($"Failed to enumerate archive files: {ex.Message}");
            return (deleted, errors, errorMessages);
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            DateTime lastWriteUtc;
            try
            {
                lastWriteUtc = File.GetLastWriteTimeUtc(file);
            }
            catch (Exception ex)
            {
                errors++;
                if (errorMessages.Count < 16)
                    errorMessages.Add($"Failed to stat archive file '{file}': {ex.Message}");
                continue;
            }

            if (lastWriteUtc >= cutoff)
                continue;

            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex)
            {
                errors++;
                if (errorMessages.Count < 16)
                    errorMessages.Add($"Failed to delete archive file '{file}': {ex.Message}");
            }
        }

        return (deleted, errors, errorMessages);
    }

    private static string EncodeId(string id)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
