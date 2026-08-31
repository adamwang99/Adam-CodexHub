# Adam CodexHub

[![Build](https://github.com/adamwang99/Adam-CodexHub/actions/workflows/build.yml/badge.svg)](https://github.com/adamwang99/Adam-CodexHub/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-D89B2B.svg)](LICENSE)

Adam CodexHub is a lightweight, Windows-first **multi-provider control hub for Codex Desktop and Codex CLI**.

It provides a WPF desktop application and CLI for managing provider profiles, encrypted API-key pools, model compatibility, Codex configuration, a loopback gateway and provider-safe session handoffs.

## Features

- built-in and custom OpenAI-compatible provider profiles backed by SQLite
- Windows DPAPI protection for API keys; plaintext keys are never persisted
- bounded same-provider key failover with cooldown and health tracking
- model discovery separated from explicit Codex compatibility verification
- transactional Codex TOML updates with backup, validation and rollback
- authenticated loopback-only gateway with streaming relay
- provider-specific session continuity without silently reusing cross-provider sessions
- WPF control hub plus companion CLI

## Stack

- C# / .NET 8
- WPF
- MVVM
- ASP.NET Core local gateway
- SQLite
- Windows DPAPI
- HttpClient
- System.Text.Json

## Critical session rule

> Do not silently convert an existing Codex chat session from one provider into another provider.

- same provider + another API key → hot failover is allowed
- same provider + compatible model → may switch after compatibility validation
- different provider → resume/create provider-specific session + synchronize current project state
- returning to an old provider session → resume + automatic stale-state refresh

## Projects

```text
src/
  AdamCodexHub.App/             WPF desktop app
  AdamCodexHub.Core/            domain models + interfaces
  AdamCodexHub.Infrastructure/  storage, DPAPI, settings
  AdamCodexHub.Providers/       provider registry + adapters
  AdamCodexHub.Codex/           Codex config + project/session state
  AdamCodexHub.Gateway/         localhost gateway host
  AdamCodexHub.Cli/             CLI companion

tests/
  AdamCodexHub.Core.Tests/
```

## Build on Windows

Requirements:

- Windows 10 or Windows 11
- .NET 8 SDK

```powershell
dotnet restore .\AdamCodexHub.sln
dotnet build .\AdamCodexHub.sln -c Debug
dotnet test .\AdamCodexHub.sln
dotnet run --project .\src\AdamCodexHub.App\AdamCodexHub.App.csproj
```

Run the CLI:

```powershell
dotnet run --project .\src\AdamCodexHub.Cli\AdamCodexHub.Cli.csproj -- providers
```

## Download

Download the latest self-contained Windows x64 package from
[GitHub Releases](https://github.com/adamwang99/Adam-CodexHub/releases/latest).

1. Download `AdamCodexHub-vX.Y.Z-win-x64.zip` and its `.sha256` file.
2. Verify the SHA-256 checksum.
3. Extract the full archive and run `AdamCodexHub.App.exe`.

The portable package includes the .NET runtime. Binaries are currently unsigned,
so Windows SmartScreen may display a warning.

## Coding-agent entry point

Read:

1. `AGENTS.md`
2. `PROJECT-CONTEXT.md`
3. `docs/14-AGENT-IMPLEMENTATION-GUIDE.md`
4. `docs/15-ACCEPTANCE-CRITERIA.md`

## Brand

Approved logo:

`assets/adam-codexhub-logo.png`

Primary colors:

- `#111111` Matte Charcoal
- `#D89B2B` Warm Amber Gold
- `#F8F4E8` Warm Cream
- `#FFFFFF` Pure White
- `#2FD0C8` Tech Aqua
- `#181818` Card Charcoal

## Security

Read [SECURITY.md](SECURITY.md) before reporting a vulnerability. Never include API keys, authorization headers, prompt contents or private source code in an issue.

## License

Adam CodexHub is released under the [MIT License](LICENSE).
