# Changelog

## v1.5.1

### Added

- **The hub follows the model you pick inside Codex.** Codex Desktop's own "Select model" picker
  never rewrites `~/.codex/config.toml` — it stores the choice per thread in
  `~/.codex/state_5.sqlite`. The hub now reads that state back (every 3s) and mirrors it into the
  Home provider card and the tray menu, which also gained a `Codex is using: <model>` line, so the
  UI can no longer disagree with what Codex is really running. A model the hub itself just
  activated keeps the screen until Codex reflects it.
- **Every tray entry carries a one-letter status tag.** `F` fast, `N` normal, `S` slow,
  `U` not checked yet, `X` skipped — appended in parentheses to each model name and to the
  `Codex is using:` line, with a legend line underneath. Colour alone is easy to miss (and
  meaningless to colour-blind eyes); the letter repeats the very same classification, so the two
  can never disagree.
- **The colour and the tag are right the moment the submenu opens.** The tray used to read an
  in-memory cache that a fresh launch left empty — and the background ping skips results that are
  still fresh — so every entry claimed "not checked yet" for hours. Opening the submenu now seeds
  the shared state from the stored results first, so a restart no longer wipes the picture.
- **Codex only sees models that really work.** A new probe (`CodexReadinessChecker`) sends the exact request Codex Desktop sends — instructions, a user turn and a function tool with `tool_choice = "required"` — through the hub's own gateway, so it exercises the real path (gateway → adapter → provider). A model is *ready* only when **both** attempts come back HTTP 200 **with an actual tool call**; one error, timeout or tool-less answer marks it not ready.
- the verdicts are persisted (`codex_readiness`, one row per provider + model, `ready` / `checked_at` / `latency_ms` / `detail`) and drive one shared rule (`CodexCatalogPolicy.SelectPublished`): the gateway's `GET /v1/models` app-server catalogue and the tray **Model** submenu publish only models with a fresh Ready verdict. Until the first verdicts exist the full enabled set is still published, so a fresh install never sees an empty picker.
- **5-minute refresh** (`CodexReadinessPingService`): not-ready or stale models of the ACTIVE provider are re-probed every 5 minutes (one attempt per unready model, a second confirmation attempt before it is published), and a model that starts answering Codex requests is offered in Codex and in the tray **within one tick** — a model that starts failing drops out just as fast. Verdicts expire after 6 h. The first tick of a provider with no verdicts at all sweeps the whole enabled list in one pass, so the catalogue is correct right after startup.
- why it matters: providers such as HHTech mix OpenAI/Codex entries (renamed with a `claude-` prefix) into the Claude list; those entries can answer plain chat but fail the tool-driven request Codex sends, which is exactly what used to show up as dead models in Codex's model picker.
- the hub UI now labels such a model in the tooltip (`lỗi trong Codex` / `fails in Codex`, with the probe's failure reason), the tray *Pause background checks* switch pauses the readiness probe too, and `CodexReadinessTests` covers the publish policy and the probe (96 tests total).

## v1.5.0

### Added

- **Codex now sees the real model list of the active provider.** Codex 0.153.4 asks the gateway for its model catalogue (`GET /v1/models?client_version=…`) and expects the app-server shape `{"models":[{ "slug": …, "display_name": …, "visibility": "list", … }]}`; the gateway used to answer with the OpenAI shape (`{"object":"list","data":[…]}`), so Codex failed to decode it (`missing field 'models'`) and silently fell back to its built-in account list. The gateway now serves both shapes — Codex gets its catalogue, every OpenAI-compatible client keeps the old one.
- the consequence: the models of the **active provider** appear in Codex's own model picker, so a model can be switched **inside a running session** (the choice is stored per thread and applies to the next turn) instead of only through the hub's launch path.
- `CodexModelCatalog` (Gateway) renders that catalogue from the same enabled-model store the hub UI uses, so the hub and Codex can never disagree about which models exist. Regression tests: `CodexModelCatalogTests`.

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
