# 15 — Acceptance Criteria

## Provider

- User can add a preset provider.
- User can add a custom compatible provider.
- Provider can be disabled without deleting it.
- Provider can be tested.
- Failed provider cannot silently become active.

## Keys

- Multiple keys can be stored per provider.
- Keys can be reordered.
- Full key is not displayed after save.
- Exhausted key is skipped.
- Rate-limited key can enter cooldown.
- Unauthorized key is not automatically retried forever.

## Models

- Model scan can add discovered entries.
- Newly discovered model is not automatically enabled by default.
- User can enable or disable model.
- Missing remote model becomes unavailable rather than being instantly deleted.
- Compatibility result is stored with timestamp.

## Config

- Existing Codex config is backed up.
- Candidate config is validated.
- Failure restores last known good config.
- Codex account profile can be restored.
- Normal application exit restores the preserved Codex account profile before stopping the gateway.
- Missing account profile does not cause a fabricated native configuration.
- Gateway shutdown continues even when account restoration fails.

## Sessions

- Different providers do not silently share one session identity.
- Switching provider saves project state.
- Returning to stale session triggers sync.
- New target-provider session receives a handoff.
- User can force full refresh.

## First run

- Session explanation is mandatory.
- Continue is disabled before acknowledgement.
- Acknowledgement version is stored.
- Remote data transfer, provider terms and billable requests are acknowledged.
- The first activation of each remote provider shows a provider-specific notice.

## Security

- Keys are not stored plaintext in SQLite.
- Logs redact Authorization header.
- Gateway binds to loopback by default.
- Gateway token is random and rotates on each gateway start.
- Gateway token comparison is constant-time.
- Remote provider URLs require HTTPS.
- HTTP provider URLs are accepted only for loopback endpoints.
