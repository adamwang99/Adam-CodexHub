# Changelog

## v1.4.0

### Added

- latency-aware model status: every compatibility probe now records `first_byte_ms` / `total_ms` (SQLite migration) and classifies the model as `Callable`, `Slow`, `Skip` or `Unknown` (`ModelCallability`, 15 s slow threshold, 6 h result TTL)
- the model name itself carries that status colour — green callable, amber slow, red skip, grey not checked yet — in the Home selector, each provider card's model dropdown and the tray **Model** submenu, with a tooltip showing the last check time and the measured latency
- every model picker is ordered callable → slow → unknown → skip (alphabetical inside a group) without changing the selected model, and the Providers page groups the model list under matching headers with a *Latency* column
- bounded background model re-check (`ModelAutoPingService`): up to 3 models of the ACTIVE provider every 15 minutes, never two checks at once, never a model whose manual test is in flight; the tray submenu gains *Pause background checks* and *Show skipped models*
- probe budget raised from 30 s to 150 s per attempt, so a genuinely slow gateway model is measured instead of being reported as broken

### Fixed

- the launch helpers (`LaunchCodexAsync`, `ChooseLogoCoreAsync`) no longer declare a pointless `async`, clearing the CS1998 build warnings

### Changed

- all README screenshots and both in-app guide sets re-shot at v1.4.0

## Unreleased

### Added

- Initial Adam CodexHub source skeleton
- WPF shell
- provider registry and presets
- secure key-vault abstraction
- Codex configuration transaction skeleton
- localhost gateway skeleton
- project/session continuity skeleton
- first-run acknowledgement
- SQLite provider/profile persistence with built-in preset seeding
- persisted active-provider selection and safe custom-provider CRUD
- key-pool reorder, enable/disable, health recovery, exclusion and secure deletion
- parser-backed Codex TOML merge with account capture and rollback-safe writes
- persisted model lifecycle, compatibility history and real OpenAI-compatible probes
- loopback-only authenticated gateway forwarding with streaming and bounded key failover
- privacy, disclaimer, trademark, provider-data and third-party dependency notices
- first-run data-transfer and billable-request acknowledgements
- per-provider remote data and cost acknowledgement
- Dependabot configuration for NuGet and GitHub Actions

### Fixed

- test project xUnit namespace and local NuGet source configuration
- WPF async startup no longer shuts down before first-run or main window creation
- WPF window resources now apply to custom window classes, restoring the intended dark theme and readable text
- API Keys no longer crashes when the password field is first rendered
- provider/model selectors use a readable dark template across normal, hover, focus and disabled states
- public MIT licensing and GitHub repository metadata
- self-contained Windows release packaging with SHA-256 verification
- gateway tokens are now cryptographically random and rotate on every gateway start
- remote custom providers now require HTTPS, while HTTP remains available for loopback providers
- release ZIPs now include legal notices, dependency inventory and .NET third-party notices
- release ZIPs now include a CycloneDX 1.5 SBOM generated from published dependency manifests
- normal desktop application exit now restores the preserved Codex Account configuration before stopping the gateway
