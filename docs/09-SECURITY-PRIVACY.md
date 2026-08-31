# 09 — Security & Privacy

## 1. Security principles

- local-first
- least privilege
- secrets protected at rest
- no prompt logging by default
- no hidden remote telemetry
- safe configuration rollback
- loopback-only gateway

## 2. API keys

Never store plaintext secrets in:

- JSON config
- SQLite
- logs
- exported provider definitions
- crash reports

Use Windows secure storage.

## 3. Gateway privacy

Default metadata logging is permitted.

Request/response body logging is OFF.

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
