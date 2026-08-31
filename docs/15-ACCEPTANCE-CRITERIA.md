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

## Security

- Keys are not stored plaintext in SQLite.
- Logs redact Authorization header.
- Gateway binds to loopback by default.
