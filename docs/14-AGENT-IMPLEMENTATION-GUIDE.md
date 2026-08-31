# 14 — Coding Agent Implementation Guide

## 1. Read-first order

Before coding:

1. `01-PRODUCT-PRD.md`
2. `02-TECHNICAL-ARCHITECTURE.md`
3. `08-SESSION-CONTINUITY-ENGINE.md`
4. `09-SECURITY-PRIVACY.md`
5. `03-UI-UX-SPEC.md`
6. remaining subsystem specifications

## 2. Architecture constraints

Do not:

- hard-code provider branching throughout UI
- store API keys plaintext
- directly mutate Codex internal chat databases
- silently hot-swap different providers in one session
- write active Codex config without backup
- block UI thread during network probes
- add every scanned model to picker automatically

## 3. Provider abstraction

Required interfaces should resemble:

```csharp
public interface IProviderAdapter
{
    Task<ProviderProbeResult> ProbeAsync(...);
    Task<IReadOnlyList<RemoteModel>> ListModelsAsync(...);
    Task<CompatibilityResult> TestModelAsync(...);
}
```

Gateway provider adapters should be independent from WPF.

## 4. Services

Suggested services:

```text
IProviderRegistryService
IProviderManager
IKeyVault
IKeyPoolService
IModelDiscoveryService
ICompatibilityService
ICodexConfigService
IGatewayService
ISessionContinuityService
IProjectStateService
IUsageService
IDiagnosticsService
```

## 5. MVVM

Use:

- CommunityToolkit.Mvvm
- dependency injection
- async commands
- observable properties

ViewModels should not perform raw HTTP or filesystem writes directly.

## 6. Implementation phases

### Phase A
Create solution, DI, logging, navigation, brand resources.

### Phase B
Provider entities, SQLite, secure key vault.

### Phase C
Codex config read/backup/restore.

### Phase D
Provider presets and custom-provider wizard.

### Phase E
Local gateway + key pool.

### Phase F
Model discovery.

### Phase G
Compatibility probes.

### Phase H
Session continuity.

### Phase I
Diagnostics, usage, polish.

## 7. Testing priorities

Highest-risk tests:

- atomic config rollback
- secret redaction
- key failover
- cancellation of streaming
- provider timeout
- malformed model response
- session stale-state comparison
- project-state serialization
- registry schema validation

## 8. Definition of done for a feature

A feature is not done until:

- UI states exist
- cancellation exists for long operations
- errors are mapped to user language
- logging is redacted
- unit tests cover domain logic
- config/state migration is considered
