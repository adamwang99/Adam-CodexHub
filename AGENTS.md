# AGENTS.md

Instructions for coding agents working on Adam CodexHub.

## Required reading

- `PROJECT-CONTEXT.md`
- `docs/01-PRODUCT-PRD.md`
- `docs/02-TECHNICAL-ARCHITECTURE.md`
- `docs/08-SESSION-CONTINUITY-ENGINE.md`
- `docs/09-SECURITY-PRIVACY.md`
- `docs/14-AGENT-IMPLEMENTATION-GUIDE.md`
- `docs/15-ACCEPTANCE-CRITERIA.md`

## Non-negotiable rules

1. Never persist plaintext API keys.
2. Never log Authorization headers or secret values.
3. Do not silently change provider identity underneath an existing Codex session.
4. Do not depend on undocumented mutation of Codex internal chat databases.
5. Active Codex config changes must be backup + candidate + validate + atomic replace + rollback capable.
6. Provider-specific branching belongs in adapters/definitions, not WPF view models.
7. `/models` discovery is not proof of Codex compatibility.
8. Long network/filesystem operations must be async and cancellable.
9. Default gateway binding is loopback only.
10. Keep the product a compact control hub, not an IDE.

## First agent task

```powershell
dotnet restore AdamCodexHub.sln
dotnet build AdamCodexHub.sln -c Debug
dotnet test AdamCodexHub.sln
```

Resolve compile/package drift before implementing features.

## Implementation order

1. make solution build
2. SQLite persistence and migrations
3. provider/profile persistence
4. key-pool persistence + tests
5. Codex config transactions
6. provider auto-detect
7. model discovery
8. compatibility probing
9. gateway routing
10. session continuity
11. UI polish
