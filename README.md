<p align="center">
  <a href="https://github.com/adamwang99/Adam-CodexHub/releases/latest">
    <img src="assets/adam-codexhub-banner.png" alt="Adam CodexHub connects Codex Desktop and CLI to multiple AI providers" width="100%">
  </a>
</p>

<p align="center">
  A secure Windows control hub for using Codex with multiple AI providers.
</p>

<p align="center">
  <a href="https://github.com/adamwang99/Adam-CodexHub/actions/workflows/build.yml"><img src="https://github.com/adamwang99/Adam-CodexHub/actions/workflows/build.yml/badge.svg" alt="Build status"></a>
  <a href="https://github.com/adamwang99/Adam-CodexHub/releases/latest"><img src="https://img.shields.io/github/v/release/adamwang99/Adam-CodexHub?display_name=tag&sort=semver&color=D89B2B" alt="Latest release"></a>
  <a href="https://github.com/adamwang99/Adam-CodexHub/releases"><img src="https://img.shields.io/github/downloads/adamwang99/Adam-CodexHub/total?color=2FD0C8" alt="Total downloads"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-D89B2B.svg" alt="MIT License"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4" alt="Windows 10 and 11">
  <img src="https://img.shields.io/badge/.NET-8-512BD4" alt=".NET 8">
</p>

<p align="center">
  <a href="https://github.com/adamwang99/Adam-CodexHub/releases/latest"><strong>Download for Windows</strong></a>
  ·
  <a href="#quick-start">Quick start</a>
  ·
  <a href="#important-read-before-first-use">Important instructions</a>
  ·
  <a href="docs/INDEX.md">Documentation</a>
</p>

---

## Overview

**Adam CodexHub** is a lightweight, Windows-first control hub for managing Codex Desktop and Codex CLI across a native Codex account and compatible third-party AI providers.

Instead of repeatedly editing `~/.codex/config.toml`, exposing API keys, or forcing one provider's chat history into another provider, Adam CodexHub gives you one place to:

- manage built-in and custom OpenAI-compatible provider profiles;
- store provider API keys in encrypted, prioritized key pools;
- discover models and verify Codex compatibility before enabling them;
- activate a provider through an authenticated loopback gateway;
- preserve and restore the native Codex account configuration;
- prepare provider-safe project handoffs without editing Codex's internal session database;
- inspect gateway and configuration recovery status from a WPF desktop app;
- automate basic operations with the companion CLI.

> [!NOTE]
> Adam CodexHub is an independent open-source project. It is not affiliated with or endorsed by OpenAI or any provider included as a preset. Provider names belong to their respective owners.

## Why Adam CodexHub?

| Common problem | Adam CodexHub approach |
| --- | --- |
| Repeated manual TOML editing | Validated, transactional Codex configuration updates |
| API keys copied into config files | Windows DPAPI encryption bound to the current user |
| One API key reaches a quota or rate limit | Ordered key pool with health, cooldown and bounded failover |
| `/models` returns a model that Codex cannot actually use | Separate discovery, compatibility testing and explicit enablement |
| Switching providers leaves stale context | Project-state snapshot plus a provider-specific continuation instruction |
| A config update fails | Backup, validation, atomic replacement and rollback support |
| A local proxy is exposed to the network | Gateway binds only to `127.0.0.1` and requires a local token |

## Key capabilities

### Provider control

- Native Codex account mode remains direct and does not use the local gateway.
- Compatible API providers are routed through a managed loopback gateway.
- Built-in presets and user-defined OpenAI-compatible providers are supported.
- Provider-specific behavior stays in adapters and declarative definitions.

### Secure API-key pools

- Secrets are encrypted with Windows DPAPI using `CurrentUser` scope.
- Plaintext API keys are not persisted in the database or provider files.
- Keys can be labeled, prioritized, enabled, disabled, reordered, tested and deleted.
- Failover is limited to healthy keys belonging to the same provider.

### Model lifecycle

- Scan a provider's advertised model catalog.
- Test text, streaming and tool-calling behavior.
- Review a compatibility score and probe history.
- Explicitly enable only verified models for Codex activation.

### Safer Codex configuration

- Parses and validates TOML before replacing the active config.
- Captures the native account config before the first gateway activation.
- Writes timestamped backups under the Codex home directory.
- Restores the last known-good configuration from Diagnostics.

### Provider-safe project handoffs

- Records Git HEAD and current working-tree changes.
- Writes `.adam-codexhub/CURRENT_STATE.md` and `project-state.json` in the selected project.
- Produces a continuation instruction for the target provider session.
- Treats the current filesystem and Git state as more authoritative than old chat history.

## Download

Download the latest self-contained package from **[GitHub Releases](https://github.com/adamwang99/Adam-CodexHub/releases/latest)**.

| Package | Use case |
| --- | --- |
| `AdamCodexHub-vX.Y.Z-win-x64.zip` | Portable Windows 10/11 x64 desktop app and CLI |
| `AdamCodexHub-vX.Y.Z-win-x64.zip.sha256` | SHA-256 value used to verify the ZIP |

The release package includes the .NET runtime. You do not need to install the .NET runtime separately when using the downloadable ZIP.

## Important: Read before first use

> [!IMPORTANT]
> Adam CodexHub changes the active Codex configuration when you activate a provider. Complete the checklist below before relying on it for project work.

### Required setup checklist

1. **Install Codex separately.** Adam CodexHub does not include Codex Desktop or Codex CLI.
2. **Sign in to your native Codex account first** if you want to switch back to it later. Confirm that `%USERPROFILE%\.codex\config.toml` exists before activating an API provider for the first time.
3. **Download both the ZIP and `.sha256` file**, then verify the checksum before extraction.
4. **Extract the entire ZIP to a normal folder.** Do not run the application from inside the compressed archive.
5. **Read and accept the first-run provider/session notice.** The Continue button remains disabled until you acknowledge it.
6. **Add and test an API key** for an API provider before scanning or testing its models.
7. **Scan, test and explicitly enable a model** before attempting to activate it. Model discovery alone is not proof of Codex compatibility.
8. **Set the real project path on the Sessions page** before switching providers if you want a handoff snapshot and continuation instruction.
9. **Start a new Codex process or session after activation.** An already-running process may still be using the previous configuration.
10. **Keep Adam CodexHub running while using an API provider.** Closing the app stops its in-process gateway. After reopening the app, activate the provider again so Codex receives the current gateway port.

> [!WARNING]
> Release binaries are currently unsigned. Windows SmartScreen may show an "unrecognized app" warning. Verify the SHA-256 checksum and confirm that the file came from this repository before choosing **More info > Run anyway**.

> [!CAUTION]
> Do not paste API keys into GitHub issues, screenshots, logs, provider JSON files, `config.toml`, or chat messages. Enter secrets only in the API Keys page. API-key charges, quotas, data handling and provider terms remain your responsibility.

### The provider-switching rule

Adam CodexHub does **not** silently convert an existing chat session from one provider into another.

- Same provider, different healthy API key: hot failover is allowed.
- Same provider, another verified model: model activation is allowed.
- Different provider: use a separate target-provider session and carry over the synchronized project state.
- Returning to an older provider: refresh stale project state before continuing.

The source-of-truth order is:

1. current project files;
2. current Git state;
3. `.adam-codexhub/CURRENT_STATE.md` and synchronized project state;
4. current project instructions;
5. older chat history.

> [!IMPORTANT]
> In `v0.1.0`, Adam CodexHub prepares the handoff files and displays a continuation instruction. It does not modify undocumented Codex chat databases or automatically move messages between providers. Read or paste the generated continuation instruction into the target provider's Codex session before continuing work.

## Quick start

### 1. Download and verify

Download the latest ZIP and checksum file from the [Releases page](https://github.com/adamwang99/Adam-CodexHub/releases/latest), place them in the same directory, then run PowerShell:

```powershell
$zip = '.\AdamCodexHub-v0.1.0-win-x64.zip'
$checksum = "$zip.sha256"

$expected = ((Get-Content $checksum -Raw).Trim() -split '\s+')[0]
$actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()

$actual -eq $expected
```

PowerShell must return `True`. If it returns `False`, delete both files and download them again from the official release page.

### 2. Extract and launch

1. Right-click the ZIP and select **Extract All**.
2. Open the extracted `AdamCodexHub-vX.Y.Z-win-x64` folder.
3. Run `AdamCodexHub.App.exe`.
4. Read the mandatory session notice, select the acknowledgement checkbox and continue.

### 3. Preserve native Codex account access

Before activating a third-party provider for the first time:

1. Install and open Codex Desktop or Codex CLI.
2. Complete the normal Codex account sign-in flow.
3. Confirm that `%USERPROFILE%\.codex\config.toml` exists.
4. Leave **Codex Account** selected until the account configuration is ready.

On the first successful API-provider activation, Adam CodexHub preserves the current account configuration as `%USERPROFILE%\.codex\config-ACCOUNT.toml`.

### 4. Configure an API provider

1. Open **Providers**.
2. Select an included provider, or choose **New** and enter a custom provider ID, display name, base URL and adapter.
3. Open **API Keys** and select the provider.
4. Enter a label, API key and priority, then choose **Add key**.
5. Select the stored key and choose **Test selected**.
6. Resolve authentication, billing or endpoint errors before continuing.

For local providers such as Ollama or LM Studio, start the provider server first and confirm that its configured base URL is reachable.

### 5. Discover and enable a model

1. Open **Models** and select the provider.
2. Choose **Scan models** to refresh the advertised catalog.
3. Select a model and choose **Test compatibility**.
4. Review the compatibility result for text, streaming and tool calling.
5. Choose **Enable / Disable** to explicitly enable a verified model.

Newly discovered models remain disabled by design.

### 6. Prepare project continuity

1. Open **Sessions**.
2. Enter the absolute path to the project you are currently working on.
3. Choose **Refresh project state**.
4. Confirm that `<project>\.adam-codexhub\CURRENT_STATE.md` was created.
5. Select the target provider and choose **Prepare provider handoff**.
6. Review the generated **Continuation instruction**.

The `.adam-codexhub` directory can contain project filenames, Git state and work summaries. Review it before committing or sharing it. It never needs to contain an API key.

### 7. Activate and use the provider

1. Select the target provider and enabled model in the top control bar.
2. Choose **Activate**.
3. Confirm that the status reports the provider, model and gateway port.
4. Start a new Codex CLI process or reopen the relevant Codex session.
5. For a cross-provider switch, provide the generated continuation instruction to the target session.
6. Keep Adam CodexHub open for the duration of API-provider use.

To return to the native account, select **Codex Account** and choose **Activate**. The managed gateway stops and the preserved account config is restored.

## Included provider presets

Adam CodexHub currently seeds these profiles:

- Codex Account
- OpenRouter
- DeepSeek
- Groq
- Mistral
- Together AI
- Fireworks AI
- xAI
- Qwen
- Ollama
- LM Studio
- Generic OpenAI-compatible provider

> [!NOTE]
> A preset is convenience metadata, not a compatibility guarantee. Provider endpoints, model names, supported APIs and authentication rules can change. Always test the key and model in the app.

## How it works

### Native account path

```text
Codex Desktop / Codex CLI
          |
          v
Native Codex account configuration
```

### Compatible API-provider path

```text
Codex Desktop / Codex CLI
          |
          v
http://127.0.0.1:<dynamic-port>/v1
          |
          v
Adam CodexHub gateway
          |
          v
Selected provider + healthy key + verified model
```

The gateway forwards requests and streaming responses, applies the selected provider adapter, and performs bounded same-provider key failover. It does not intentionally expose a LAN or public listener.

## Security and local data

### Security defaults

- API keys are encrypted with Windows DPAPI and scoped to the current Windows user.
- Authorization headers and secret values must not be logged.
- Prompt and response body logging is off by default.
- The gateway binds to loopback only.
- Codex config updates are backed up and validated before activation.
- Provider definitions cannot store secret-bearing headers.

> [!WARNING]
> DPAPI-protected keys are not portable to another Windows account or machine. When moving Adam CodexHub, reinstall the app and enter the API keys again instead of copying the encrypted secret files.

### Data locations

| Data | Default location |
| --- | --- |
| App database | `%LOCALAPPDATA%\AdamCodexHub\data\adam-codexhub.db` |
| Encrypted key files | `%LOCALAPPDATA%\AdamCodexHub\secrets\` |
| App settings | `%LOCALAPPDATA%\AdamCodexHub\data\settings.json` |
| Active Codex config | `%USERPROFILE%\.codex\config.toml` |
| Preserved account config | `%USERPROFILE%\.codex\config-ACCOUNT.toml` |
| Codex config backups | `%USERPROFILE%\.codex\adam-codexhub-backups\` |
| Project handoff state | `<project>\.adam-codexhub\` |

Read [SECURITY.md](SECURITY.md) before reporting a vulnerability. Never include API keys, authorization headers, prompt contents or private source code in a public issue.

## CLI companion

The portable package places the CLI under `cli\AdamCodexHub.Cli.exe`.

```powershell
# Show Codex home and account-profile status
.\cli\AdamCodexHub.Cli.exe status

# List provider profiles
.\cli\AdamCodexHub.Cli.exe providers

# Refresh handoff state for a project
.\cli\AdamCodexHub.Cli.exe refresh 'C:\path\to\project'

# Run the local gateway until Ctrl+C
.\cli\AdamCodexHub.Cli.exe gateway
```

The GUI and CLI use the same local database, encrypted key vault and Codex home for the current Windows user.

## Troubleshooting

### Windows SmartScreen blocks startup

Verify the release checksum first. Then open **More info**, confirm the publisher warning and choose **Run anyway** only if the package came from this repository.

### No model appears in the activation selector

Open **Models**, scan the provider, test the desired model and explicitly enable it. Only verified, enabled models appear in the activation selector.

### Codex cannot connect to `127.0.0.1`

- Confirm Adam CodexHub is still open.
- Activate the provider again after every app restart so the active config receives the current dynamic gateway port.
- Check **Diagnostics** for the gateway status.
- Confirm local security software is not blocking loopback traffic.

### The provider rejects authentication

- Confirm the correct provider is selected on **API Keys**.
- Test the selected key.
- Check provider billing, quota, organization and regional restrictions.
- Make sure a custom provider uses the correct base URL and adapter.

### A model is listed but fails in Codex

Discovery only confirms that the provider advertised the model. Run compatibility testing and inspect text, streaming and tool-calling results. Provider behavior can change after a previous successful test.

### Codex Account cannot be restored

The preserved account profile may not exist if an API provider was activated before native Codex sign-in created `config.toml`. Sign in with native Codex, recreate the account configuration and verify it before switching again.

### The Codex config is invalid or stale

Open **Diagnostics** and choose **Restore last known good**. Backups are stored under `%USERPROFILE%\.codex\adam-codexhub-backups\`.

### Keys stopped working after moving files to another PC

This is expected because DPAPI encryption is bound to the original Windows user. Add the provider keys again on the destination machine.

## Build from source

### Requirements

- Windows 10 or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Git
- Codex Desktop and/or Codex CLI for integration testing

### Restore, build and test

```powershell
git clone https://github.com/adamwang99/Adam-CodexHub.git
cd Adam-CodexHub

dotnet restore .\AdamCodexHub.sln
dotnet build .\AdamCodexHub.sln -c Debug
dotnet test .\AdamCodexHub.sln
```

### Run the desktop app

```powershell
dotnet run --project .\src\AdamCodexHub.App\AdamCodexHub.App.csproj
```

### Run the CLI

```powershell
dotnet run --project .\src\AdamCodexHub.Cli\AdamCodexHub.Cli.csproj -- providers
```

### Create a self-contained package

```powershell
.\scripts\package-release.ps1 -Version 0.1.0 -RuntimeIdentifier win-x64
```

The ZIP and `.sha256` file are written to `artifacts\`.

## Repository structure

```text
src/
  AdamCodexHub.App/             WPF desktop app
  AdamCodexHub.Core/            domain models and interfaces
  AdamCodexHub.Infrastructure/  SQLite, DPAPI, settings and paths
  AdamCodexHub.Providers/       provider registry and adapters
  AdamCodexHub.Codex/           Codex config and project/session state
  AdamCodexHub.Gateway/         authenticated loopback gateway
  AdamCodexHub.Cli/             command-line companion

tests/
  AdamCodexHub.Core.Tests/      domain and integration-focused tests

docs/                           product, architecture and UX specifications
scripts/                        bootstrap, run and release packaging scripts
schemas/                        provider definition schema
examples/                       example custom provider definition
```

## Development principles

Contributions must preserve these non-negotiable rules:

1. Never persist plaintext API keys.
2. Never log authorization headers or secret values.
3. Never silently change provider identity underneath an existing Codex session.
4. Never depend on undocumented mutation of Codex internal chat databases.
5. Keep active config changes transactional, validated and rollback-capable.
6. Keep provider-specific behavior in adapters or provider definitions, not WPF view models.
7. Treat model discovery and Codex compatibility as separate states.
8. Keep the default gateway loopback-only.

Start with [CONTRIBUTING.md](CONTRIBUTING.md), [DEVELOPMENT.md](DEVELOPMENT.md) and [AGENTS.md](AGENTS.md). The complete documentation map is available in [docs/INDEX.md](docs/INDEX.md).

## Project status

Adam CodexHub is an early open-source release. Test with non-critical projects first, review generated project state, and keep independent backups of important work and Codex configuration.

- [Release history](CHANGELOG.md)
- [Roadmap](docs/13-ROADMAP.md)
- [Acceptance criteria](docs/15-ACCEPTANCE-CRITERIA.md)
- [Report an issue](https://github.com/adamwang99/Adam-CodexHub/issues)

## License

Adam CodexHub is released under the [MIT License](LICENSE). You may use, copy, modify and distribute it under the terms of that license. The software is provided without warranty.
