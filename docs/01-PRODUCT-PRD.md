# 01 — Product Requirements Document

## 1. Product name

**Adam CodexHub**

## 2. Product definition

Adam CodexHub is a Windows-first desktop application that manages Codex Desktop and Codex CLI across:

- standard OpenAI/Codex account login
- major API providers with built-in presets
- OpenAI-compatible providers
- Responses-compatible providers
- local inference services
- user-added custom providers

The product should feel like a lightweight control panel rather than a large IDE.

## 3. Problem statement

Using Codex with alternative providers is technically possible but fragile when users manually edit configuration files.

Main problems:

- provider configuration is error-prone
- API keys are hard to test and rotate
- users cannot easily see provider health
- model lists change frequently
- `/models` may advertise models that do not actually work with Codex tools
- different providers expose different protocols and capabilities
- switching provider underneath an existing chat thread can break session resume
- old sessions become stale after another provider changes the same project
- manual configuration makes recovery difficult

Adam CodexHub solves these as a managed workflow.

## 4. Primary users

### A. Power users
People who use Codex daily and want lower-cost or specialized models.

### B. Developers
Users who want different models for coding, vision, reasoning or batch work.

### C. Multi-account / multi-key users
Users with several valid API keys for the same provider who need failover.

### D. Local AI users
Users running Ollama, LM Studio or compatible local endpoints.

## 5. Core user goals

The user should be able to:

1. Use normal Codex account login without destroying first-party capabilities.
2. Add an API provider by pasting only the minimum required information.
3. Store many API keys under one provider.
4. Test one or all keys.
5. Reorder key priority using drag-and-drop.
6. Automatically skip exhausted, unauthorized or rate-limited keys.
7. Discover models from provider APIs.
8. Enable only selected models in Codex.
9. Remove or hide unwanted models.
10. Verify model compatibility instead of trusting model names.
11. Switch providers without corrupting existing Codex sessions.
12. Return to an older provider session and receive updated project context automatically.
13. Restore the last working configuration if anything fails.
14. Export provider definitions without exporting secrets.
15. Update provider/model metadata without reinstalling the entire application.

## 6. Product modes

### 6.1 Codex Account Mode

Uses normal OpenAI/Codex login.

This mode is intentionally treated separately from API gateway providers.

Goals:

- preserve standard account login
- preserve first-party capabilities
- do not inject third-party credentials into this mode
- allow fast return to the account configuration

### 6.2 API Provider Mode

API providers are handled through Adam CodexHub's local gateway where possible.

Benefits:

- stable local endpoint
- API-key rotation
- health monitoring
- usage metadata
- provider abstraction
- retry and failover
- reduced repeated mutation of `config.toml`

## 7. Provider presets

V1 should include presets for commonly requested provider categories, but architecture must never depend on a fixed provider list.

Suggested built-in presets:

- OpenAI / Codex account
- DeepSeek
- Qwen / DashScope
- OpenRouter
- Anthropic
- Google Gemini
- xAI
- Mistral
- Groq
- Together AI
- Fireworks AI
- Cerebras
- SiliconFlow
- Moonshot / Kimi
- Ollama
- LM Studio
- Generic OpenAI Compatible
- Generic Responses Compatible
- Generic Anthropic Compatible

Presets are configuration metadata, not hard-coded control flow.

## 8. Provider discovery

User can choose **Add Provider** and enter:

- provider display name
- base URL
- API key

Then select **Auto Detect**.

The app should probe supported API dialects and endpoints, then propose a provider profile.

User must approve before the provider becomes active.

## 9. Model lifecycle

Model states:

`Discovered -> Tested -> Verified -> Enabled -> Disabled/Unavailable/Deprecated`

The app should not place every discovered model into the Codex model selector automatically.

## 10. Session continuity rule

A session belongs to the provider that created it.

Provider switching must not silently convert one session to another.

Different provider:

- save current project state
- select or create target-provider session
- synchronize latest project state
- resume safely

Returning to old provider:

- locate previous session
- compare last-seen project revision
- refresh stale state automatically
- resume

## 11. First-use acknowledgement

On first launch, show a mandatory modal explaining provider/session behavior.

The Continue button remains disabled until the user checks:

> I understand that each provider may use a separate session and that Adam CodexHub may automatically synchronize or refresh project state when switching or resuming sessions.

Store:

- acknowledgement boolean
- acknowledgement version
- timestamp

If the mechanism changes materially, increment acknowledgement version.

## 12. Success metrics

V1 is successful when:

- normal Codex account mode can be restored in one click
- at least one compatible custom provider can be added without manually editing TOML
- key rotation works without breaking the active provider session
- model scanning works
- invalid/unavailable models do not silently become active
- configuration changes are atomic and recoverable
- users are warned before unsafe provider/session actions
- returning to a stale session triggers automatic state refresh
