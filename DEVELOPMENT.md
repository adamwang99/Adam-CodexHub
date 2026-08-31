# Development

## Requirements

- Windows 10/11
- .NET 8 SDK
- Git
- Codex Desktop and/or Codex CLI for integration testing

## First build

```powershell
dotnet restore
dotnet build AdamCodexHub.sln
dotnet test AdamCodexHub.sln
```

## Run desktop

```powershell
dotnet run --project src/AdamCodexHub.App
```

## Runtime paths

```text
%LOCALAPPDATA%\AdamCodexHub\
%USERPROFILE%\.codex\
```

Never commit real API keys.
