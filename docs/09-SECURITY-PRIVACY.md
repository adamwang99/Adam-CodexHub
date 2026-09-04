# 09 — Security & Privacy

## 1. Security principles

- local-first
- least privilege
- secrets protected at rest
- no prompt logging by default
- no hidden remote telemetry
- safe configuration rollback
- loopback-only gateway
- HTTPS-only remote provider transport

## 2. API keys

Never store plaintext secrets in:

- JSON config
- SQLite
- logs
- exported provider definitions
- crash reports

Use Windows secure storage.

DPAPI protects secrets at rest but does not protect against malicious software running as the same Windows user.

## 3. Gateway privacy

Default metadata logging is permitted.

Request/response body logging is OFF.

Remote providers may receive prompts, source code, files, outputs and metadata. Display a provider-specific disclosure before first activation and link to the provider's current terms and privacy resources.

Key tests, compatibility probes, retries and failover make real requests and may incur charges. This must be disclosed before remote use.

If user enables diagnostic body logging:

- display warning
- define retention
- allow one-click erase

## 4. Registry

Remote registry definitions are data only.

Never execute registry-provided code or scripts.

## 5. Export

Normal export:

- provider profiles
- model selections
- priorities
- settings

No secrets.

Secret export must be a separate explicit workflow with a strong warning.

## 6. Logs

Redact patterns matching:

- bearer tokens
- API keys
- auth headers
- cookies

## 7. File safety

Before modifying Codex configuration:

- backup
- validate
- atomic replace
- verify

Maintain last-known-good state.

## 8. Provider transport

Require HTTPS for every remote provider URL. Permit HTTP only when `Uri.IsLoopback` is true for a local endpoint. Reject credentials embedded in provider URLs.
