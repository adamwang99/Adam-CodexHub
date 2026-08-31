# Adam CodexHub — Project Context

## Product

A Windows-first multi-provider manager for Codex Desktop and Codex CLI.

## Confirmed decisions

- Product name: **Adam CodexHub**
- Technology: **C# + .NET 8 + WPF**
- Hybrid design:
  - normal Codex/OpenAI account login remains native/direct
  - compatible API providers use a managed localhost gateway
- provider list is extensible and user-defined providers are supported
- API keys are stored securely and may be pooled per provider
- model lists can be discovered and updated automatically
- discovered models are not automatically considered verified
- cross-provider switching uses session continuity rather than mutating an existing session
- returning to a previous provider normally resumes its previous session and auto-refreshes stale project state
- unfinished work is automatically synchronized before switching
- first-run acknowledgement of the session mechanism is mandatory
- approved logo is `assets/adam-codexhub-logo.png`

## Source of truth priority

1. current filesystem
2. Git state
3. shared project state
4. current project instructions
5. old chat history

## Read before implementation

`docs/14-AGENT-IMPLEMENTATION-GUIDE.md`
