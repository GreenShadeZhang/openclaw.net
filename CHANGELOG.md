# Changelog

All notable changes to this project are tracked in this file.

## [Unreleased] - 2026-03-04

### Security
- Bound tool-approval decisions to the original requester (`channelId` + `senderId`) for non-loopback/public binds.
- Kept `POST /tools/approve` as an explicit admin override path.
- Added WhatsApp official webhook signature validation support (`ValidateSignature`, `WebhookAppSecret`/`WebhookAppSecretRef`).
- Added WhatsApp bridge inbound auth validation via `Authorization: Bearer <BridgeToken>` or `X-Bridge-Token`.
- Enforced additional non-loopback startup hardening:
  - WhatsApp official mode requires signature validation + app secret.
  - WhatsApp bridge mode requires a bridge token.
  - `raw:` secret detection now scans the full config object graph (not just Twilio auth token refs).
- Hardened generic webhook HMAC verification:
  - `ValidateHmac=true` now requires a secret at config validation time.
  - Signature checks now use constant-time byte comparison and support `sha256=<hex>` header format.
- Hardened SQL write detection in `database` tool by tokenizing SQL and detecting write/admin keywords beyond naive prefix checks.
- Hardened `inbox_zero` IMAP command construction:
  - Quoted IMAP credentials and folders.
  - Sanitized user-provided folder names for analyze/cleanup/trash-sender actions.

### Memory Retention and Hardening
- Added opt-in memory retention configuration at `OpenClaw:Memory:Retention`:
  - `Enabled` (default `false`)
  - `RunOnStartup` (default `true`)
  - `SweepIntervalMinutes` (default `30`)
  - `SessionTtlDays` (default `30`)
  - `BranchTtlDays` (default `14`)
  - `ArchiveEnabled` (default `true`)
  - `ArchivePath` (default `./memory/archive`)
  - `ArchiveRetentionDays` (default `30`)
  - `MaxItemsPerSweep` (default `1000`)
- Added retention store abstraction (`IMemoryRetentionStore`) and new retention models:
  - `RetentionSweepRequest`
  - `RetentionSweepResult`
  - `RetentionStoreStats`
  - `RetentionRunStatus`
- Implemented retention sweep support in both file and sqlite memory stores.
- Added archive-before-delete behavior for expired sessions/branches with raw JSON archive envelopes.
- Added archive TTL purge behavior for old archive files.
- Added sqlite indexes to improve retention candidate queries:
  - `idx_sessions_updated_at`
  - `idx_branches_updated_at`
- Added proactive in-memory active-session expiry sweep (`SessionManager.SweepExpiredActiveSessions`) and wired it into periodic cleanup.
- Added background retention sweeper service (`PeriodicTimer`, overlap-safe with semaphore).
- Added retention admin endpoints:
  - `GET /memory/retention/status`
  - `POST /memory/retention/sweep` (supports `dryRun=true`)
- Extended doctor outputs (`/doctor`, `/doctor/text`) with retention config/status/stats and disabled-retention warnings for large persisted counts.
- Extended runtime metrics with retention counters and last-run status gauges.
- Corrected compaction validation semantics: when compaction is enabled, `CompactionThreshold` must be greater than `MaxHistoryTurns`.

### Usability/Safety Balance
- WebChat token persistence now defaults to session-only storage (`sessionStorage`).
- Added a `Remember` toggle to opt into persistent token storage (`localStorage`).

### Tests
- Added `ToolApprovalServiceTests` for requester-bound approvals and admin override behavior.
- Added `GatewaySecurityHardeningTests` for public-bind hardening checks (WhatsApp and raw refs).
- Expanded `GatewaySecurityTests` for HMAC signature validation.
- Expanded `ConfigValidatorTests` for webhook-HMAC-secret and WhatsApp-app-secret validation.
- Added retention validation coverage in `ConfigValidatorTests`.
- Added `FileMemoryStoreRetentionTests` (archive/delete, protected sessions, archive failure handling, archive purge).
- Added `SqliteMemoryStoreRetentionTests` (archive/delete, protected sessions, max item cap, index creation, archive failure handling).
- Added `MemoryRetentionSweeperServiceTests` (manual sweep status/metrics and overlap prevention).
- Added proactive expiry coverage in `SessionManagerTests` (`SweepExpiredActiveSessions`).
- Expanded `NativePluginTests` with SQL write-bypass regression cases.
- Expanded `SecurityTests` with InboxZero folder-sanitization coverage.

### Documentation
- Updated:
  - `README.md`
  - `USER_GUIDE.md`
  - `SECURITY.md`
  - `CHANGELOG.md`
  - `TOOLS_GUIDE.md`

### Docker
- Fixed Docker runtime env var binding to use `OpenClaw__...` (ASP.NET configuration) for gateway bind/port/memory settings.
- Docker defaults now disable the JS plugin bridge on non-loopback binds (`OpenClaw__Plugins__Enabled=false`) unless explicitly enabled.
- Standardized default image name to `openclaw.net` for local builds and compose.
- Re-pushed Docker Hub images without provenance/SBOM to improve Docker Hub UI compatibility.
- Added `DOCKERHUB.md` as paste-ready repository overview content for Docker Hub.
