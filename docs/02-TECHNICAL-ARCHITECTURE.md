# 02 — Technical Architecture

## 1. Architecture objective

Keep the UI simple while separating provider, routing, storage and Codex integration logic.

## 2. Proposed solution structure

```text
AdamCodexHub.sln

src/
  AdamCodexHub.App/             WPF UI
  AdamCodexHub.Core/            domain logic
  AdamCodexHub.Infrastructure/  SQLite, secure storage, filesystem
  AdamCodexHub.Gateway/         localhost API gateway
  AdamCodexHub.Codex/           Codex config/session integration
  AdamCodexHub.Providers/       provider adapters + registry
  AdamCodexHub.Cli/             optional CLI companion

tests/
  AdamCodexHub.Core.Tests/
  AdamCodexHub.Gateway.Tests/
  AdamCodexHub.Provider.Tests/
  AdamCodexHub.Integration.Tests/
```

## 3. Core domains

### ProviderEngine
Responsible for:

- provider profiles
- provider capabilities
- provider health
- provider activation
- provider registry sync

### KeyPoolEngine
Responsible for:

- secure key references
- key ordering
- health
- cooldown
- failover
- test status

### ModelDiscoveryEngine
Responsible for:

- model enumeration
- normalization
- filtering
- model changes
- model metadata cache

### CompatibilityEngine
Responsible for verified capabilities:

- text
- Responses API
- chat completions
- tool calling
- streaming
- JSON
- image input
- reasoning
- context behavior

### SessionContinuityEngine
Responsible for:

- provider affinity
- project revisions
- stale session detection
- shared project state
- handoff preparation
- safe resume

### CodexConfigEngine
Responsible for:

- discovering Codex config paths
- parsing TOML
- validating output
- backup
- atomic write
- rollback
- account mode preservation
- model catalog generation

### GatewayEngine
Responsible for:

- localhost endpoint
- routing
- key selection
- provider request forwarding
- retry policy
- usage metadata
- health updates

## 4. Dependency rule

UI depends on application services.

Application services depend on domain interfaces.

Infrastructure implements domain interfaces.

Provider-specific logic must not leak into view models.

## 5. Local gateway

Recommended listener:

```text
http://127.0.0.1:<dynamic-or-configured-port>
```

Never bind to `0.0.0.0` by default.

Codex API-provider profiles can point to the gateway while Adam CodexHub maps:

```text
logical provider/model
        ->
actual provider endpoint + active key
```

## 6. Atomic configuration workflow

Every config change:

```text
Read current
  ->
Backup current
  ->
Build candidate
  ->
Validate syntax
  ->
Write temporary file
  ->
Atomic replace
  ->
Verify
  ->
Launch/reload
```

If verification fails:

```text
Rollback last known good
```

Never partially rewrite the active Codex config.

## 7. Storage

Use SQLite for structured state.

Do not store API key plaintext in SQLite.

Store only a secure-secret reference.

Suggested local app directory:

```text
%LOCALAPPDATA%\AdamCodexHub\
  data\
  cache\
  logs\
  backups\
  registry\
```

Project-local continuity data:

```text
<project>\.adam-codexhub\
  CURRENT_STATE.md
  project-state.json
  session-index.json
  handoffs\
```

Allow users to choose whether project-local metadata is Git ignored.

## 8. Background operations

Use async tasks with cancellation.

Long operations:

- model scan
- compatibility test
- bulk key test
- registry sync
- project refresh

must not block WPF UI thread.

## 9. Error philosophy

Errors shown to user should answer:

1. what failed
2. what Adam CodexHub did
3. whether work/config is safe
4. recommended action

Example:

```text
DeepSeek key #2 returned insufficient quota.
Adam CodexHub switched to key #3.
Your session was not restarted.
```

## 10. Do not rely on Codex private database internals

Do not make session continuity depend on undocumented direct mutation of Codex internal databases.

Prefer:

- stable config files
- public CLI features
- filesystem project state
- process launching
- explicit user-visible handoff

If a stable Codex session API later exists, implement it behind an adapter.
