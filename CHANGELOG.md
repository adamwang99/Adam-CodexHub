# Changelog

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

### Fixed

- test project xUnit namespace and local NuGet source configuration
- WPF async startup no longer shuts down before first-run or main window creation
- WPF window resources now apply to custom window classes, restoring the intended dark theme and readable text
- API Keys no longer crashes when the password field is first rendered
- provider/model selectors use a readable dark template across normal, hover, focus and disabled states
- public MIT licensing and GitHub repository metadata
