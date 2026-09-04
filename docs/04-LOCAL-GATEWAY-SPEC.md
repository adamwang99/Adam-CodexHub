# 04 — Local Gateway Specification

## 1. Purpose

The local gateway gives Codex a stable local endpoint while Adam CodexHub handles API-provider routing.

This is not used to replace normal Codex account login.

## 2. Default behavior

Bind only to loopback:

```text
127.0.0.1
```

Default port may be fixed or dynamically selected.

Example:

```text
http://127.0.0.1:18771/v1
```

## 3. Responsibilities

- receive compatible Codex API requests
- resolve logical provider/model
- select healthy API key
- forward request
- stream response
- interpret failures
- update health
- retry safely
- collect metadata
- never persist body by default

## 4. Key failover

Retry with another key when safe:

- quota exhausted
- rate limited after configured policy
- temporary provider error

Do not retry endlessly.

## 5. Provider failover

Do not automatically fail over to a different provider inside an active chat session by default.

Cross-provider failover changes session semantics.

Default behavior:

```text
provider unavailable
  ->
notify user
  ->
offer safe switch + session handoff
```

## 6. Logging

Default metadata:

- timestamp
- provider
- model
- key alias, never full secret
- HTTP status
- latency
- token usage if available
- retry count
- error category

Default excluded:

- prompt body
- response body
- attachments
- source code contents

## 7. Gateway security

- loopback only
- cryptographically random token generated on every gateway start
- constant-time token verification
- reject external host access
- no CORS wildcard
- redact secrets in logs
- enforce request size limits
- configurable timeout

The token is written to the active Codex configuration for the current gateway process. It is local access control, not protection against malicious software running as the same Windows user.

## 8. Application shutdown

On a normal desktop application exit:

1. restore the preserved Codex Account configuration when available;
2. mark Codex Account as the active provider after successful restoration;
3. stop the in-process gateway;
4. stop and dispose the application host.

Gateway shutdown must still occur if account restoration fails. A crash, forced process termination, operating-system failure or power loss may bypass this sequence, so recovery through Codex Account activation and last-known-good backups remains necessary.

## 9. Model mapping

Support logical aliases:

```text
FAST
SMART
VISION
CHEAP
```

Mapping example:

```text
SMART -> deepseek-v4-pro
VISION -> deepseek-v4-flash-vision-exp
```

Alias mapping must not hide provider/session changes from Session Continuity Engine.
