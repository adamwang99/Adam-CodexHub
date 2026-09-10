# Changelog

## v1.5.4

### Fixed

- **Activating a provider no longer throws you into a new chat.** The session hand-off opened a fresh
  Codex chat by default (`openFreshChat: true`) and dropped the recap into it, which is the opposite
  of what a switch is for: the chat already on screen is where the work is. Fresh chat is off by
  default now (the recap switch stays), the hub re-points the open threads' model pins instead, and
  the existing setting on this machine was flipped with a backup (`codex-handoff.json.bak-*`).

- **A provider switch now reaches the chats that are already open — no new chat needed.** Every Codex
  thread carries its own pinned model, so switching provider used to leave the open chat pointing at
  the previous provider's model: on 2026-09-11 the “test video factory” chat kept
  `claude-opus-4-8[1M]` after the account was active again and Codex refused it outright — *“The
  'claude-opus-4-8[1M]' model is not supported when using Codex with a ChatGPT account.”* The repair
  that rebinds those threads existed but refused to write whenever `codex` was running, which is
  exactly when it is needed, so it never ran on a real machine. It now writes with a 4-second busy
  timeout while Codex holds the file, keeps one backup per six hours (`state_5.sqlite.hub-backup`)
  and logs what it moved (*“Codex is running — repairing N thread(s) pinned to a model this provider
  does not offer”*). The repair also runs **at startup**, so a hub restart alone fixes the chats that
  are already open — measured on this machine at 15:12:38: *“repairing 6 thread(s) … (→ gpt-5.6-sol)”*,
  backup written, and the database went from 3 chats pinned to `claude-opus-*` to zero. **Verified
  end-to-end later the same day:** the chat “test video factory”, stuck on four consecutive
  `Error running remote compact task` turns, completed in 25.5 s on the new provider
  (`responses: model=gpt-5.6-sol provider=hhtech -> 200`, no compaction error) without a new chat; and
  a model switch inside one provider (`5.5` → `claude-opus-4-7[1M]` at 15:36:11) was served as asked on
  the very next turn (`model=claude-opus-4-7[1M] -> 200`).

- **A model from another family can work in Codex again: the identity prompt is no longer a lie.**
  Codex stamps every request with *"You are Codex, an agent based on GPT-6 …"*. Measured
  2026-09-11: HHTech's Claude read that as a false system prompt — *"I'm noticing an attempt to
  inject false system instructions claiming I'm 'Codex' based on GPT-6"* — refused the tools it was
  offered and answered as a chat-only assistant, which is why the video work that was supposed to
  run on Claude stalled. The gateway now replaces the claim with an honest harness framing
  (`HarnessPrompt`) before the request leaves the machine, and says so in the log
  (*replaced the client's identity prompt before forwarding*). Nothing else in the request changes:
  the tools, the input and the model id stay exactly as Codex sent them.
- **The log says which tools a turn actually carries** (*tools offered to &lt;model&gt;: none*).
  A thread that answers “I have no exec here” is not a lying model: measured the same day, the old
  thread's requests carried `none`, while a fresh Codex session on the same Claude model ran
  `echo XINCHAO_OK` and reported `XINCHAO_OK` (UI: “Worked for 9s”). One grep tells the two apart.

- **The model catalogue keeps itself current — no more detour through *Quét mô hình*.** The hub
  only ever learned a provider's models when somebody pressed the scan button on the Setup page, so
  the list went stale behind the provider: DeepSeek shipped V4.1 Flash (`deepseek-flash`) on
  2026-09-10 and the hub still held the three ids it saw on 06-09, while the retired names stayed in
  the pickers. A background sweep now re-reads every enabled provider's `GET /models` shortly after
  launch (20 s later, off the UI thread) and then every 15 minutes, one provider at a time, skipping
  any provider whose catalogue was read inside the freshness window. The first sweep on this
  machine refreshed 5 of 17 catalogues in ~32 s and `deepseek-flash` appeared on its own, with the
  two retired names moved to `Unavailable`. Startup is untouched by design: the sweep waits for the
  window to be up and never runs on the dispatcher thread.
- **A provider that comes back from a quota outage no longer needs a manual scan.** A failed
  compatibility run used to clear a model's `enabled` flag together with its state, so one 401/429
  wave — HHTech out of quota — silently un-picked 72 models and left the provider card with nothing
  to activate; the only way back was Setup → *Quét mô hình* and re-enabling by hand. The flag is the
  user's choice now and survives a failing test run (the state alone keeps a broken model out of the
  pickers, and lets the readiness probe report it in red), and a scan restores models that were
  un-picked that way (`Failed` and previously verified) while leaving models the user turned off
  alone (`Disabled`). On this machine the 72 HHTech ids and 17 TTMAPI ids in that state come back
  with the next successful sweep.
- **A key parked by an outage is re-probed on its own, so the provider really does come back.** A
  401/402 was persisted as a permanent verdict and re-tested only at the next launch, which left
  the card reading *Warning — API key needed* for as long as the outage lasted even after the
  provider had started answering again. The sweep now looks for work every 15 minutes instead of
  every 6 hours (a tick with nothing due costs no request), and a provider that keeps failing is
  asked on a doubling backoff — 15 min, 30, 1 h … capped at the 6-hour freshness window — so a
  returned quota is noticed within minutes while a provider that is simply down costs one request
  every 6 hours. Each retry re-probes up to two of its keys first — never
  the same key more than once every 30 minutes, and never touching a provider that already has a
  working key. This is how HHTech returned on 2026-09-11 at 00:21:54 after its quota came back: the
  probe flipped the key back to `Healthy`, the catalogue was re-read in the same tick, and the 72
  un-picked models came back with it.
- **Double-clicking a card that is not ready tries to fix it before complaining.** If the card is
  not activatable the hub re-probes that provider's key and re-reads its catalogue once, then
  rebuilds the cards; only if it is still not ready does it report the reason. That removes the
  Setup → *Test keys* trip that a quota outage used to force.
- **A finished sweep redraws the Home cards.** The cards are built from the stored state, so a
  provider that recovered while the page was open used to keep showing *SETUP / Warning* until the
  app was restarted; the sweep now announces each provider it refreshed and the page rebuilds
  itself (marshalled to the UI thread).
- **Activating a provider whose catalogue is empty rescans that one provider first.** When the card
  has no usable model the hub re-reads that provider and only then reports "no enabled model", so a
  provider that recovered seconds earlier works on the first double-click instead of sending the
  user to the Setup page.

- **The Codex picker no longer trickles in one model at a time.** Codex only offers what the
  readiness probe has recently verified, and that probe checked four models every five minutes — on
  an HHTech list of 80-odd ids the picker read five models and stayed there. The first tick of a run
  now sweeps every model whose verdict is missing or stale, callable models first, so the real list
  is there within a minute of launch (this machine: 5 → 16 models in the first pass, the rest
  following as their turn comes). Models that already proved they cannot serve a request are not
  probed at all — HHTech publishes image endpoints under text-looking ids and each one cost a probe.
- **A probe can no longer park a model, and a busy provider can no longer empty the picker.** The
  provider adapters wait up to 150 seconds per model, so a single dead id held a sweep for two and a
  half minutes and dozens of them made a pass effectively endless; each probe now gets 45 s, and a
  model that runs out of budget is left alone for an hour instead of being re-bought every tick. An
  answer that comes back 5xx/429 is read as the provider being busy rather than as a verdict: the
  stored verdict stands (a blip used to write fresh "not ready" rows for the whole list — exactly how
  the picker empties itself) and the pass stops after three refusals in a row. Observed on this
  machine: HHTech answered 503 for `gpt-5.6-sol`, `gpt-5.6-terra` and `gpt-6-astra` at 01:07 and
  "ready in 2-3 s" for all three a minute later.
- **A thread no longer keeps the previous source's route.** The migration rewrote the model a thread
  was pinned to but left `model_provider` alone, so a thread created under the ChatGPT account stayed
  routed there after a switch to the gateway — Codex answered every gateway model in it with *"not
  supported when using Codex with a ChatGPT account"* even though `config.toml` named the gateway.
  The switch now moves both columns: the model only when the target provider does not offer it, the
  route whenever it differs from the source being activated (`adam_codexhub` for a provider,
  `openai` for the account). Still closed-Codex-only, still model/provider columns only.

- **A pass probes two models at a time, and a sweep reads three providers at a time.** The readiness
  probe is what decides which models Codex may list, so one slow model used to hold the whole
  catalogue behind it; it now runs two probes in flight, each still under its 45 s budget, with the
  answers stored one at a time and a refusal still ending the pass. Catalogue sweeps read three
  providers at once — they do not share a key, so a full sweep of 17 catalogues went from a
  two-minute queue to ~12 s (measured on this machine on 2026-09-11). The models of a single
  provider are still read one request at a time: that key is shared, and HHTech answers 429/503 when
  it is crowded. Both behaviours have a test that fails as soon as the concurrency is taken away.
- **A pass with nothing to do says so.** "Finished" and "wedged" both looked like an empty log; the
  Codex probe and the auto-ping now log `nothing due: …` when every model already has a current
  verdict. (That silence cost an hour of hunting a hang that did not exist — a process dump showed no
  blocked threads and no pending work.)

- **The Codex list no longer comes up as a wall of "(U)" while the models behind it are fine.** Two
  separate faults were behind that: a 5xx/429 that answered every probe was saved as a score-0
  result (the model then looked unusable for the whole 6 h verdict window, and its entry left the
  catalogue), and three refusals in a row abandoned the entire pass — so the models the user can
  actually run were never re-verified while HHTech's 80-odd image endpoints, which refuse every
  probe, took the turns. A refusal now keeps the stored verdict, parks only that model for 15
  minutes and lets the pass carry on, and a caller's budget running out is reported as a timeout
  instead of "not ready" (one slow response used to park a working model for six hours).
- **Every pass now measures what the user can actually run first.** Both the Codex probe and the
  auto-ping ask whether a model has ever answered before anything else, so a model whose verdict
  merely lapsed is re-checked ahead of an id that never has. Measured on this machine on 2026-09-11:
  the picker went from 4 tagged models plus 80-odd "(U)" to 10+ entries with (F) tags within two
  minutes of launch, with `score 80–100` reported for claude-v4-pro, deepseek-v4-pro,
  claude-opus-4-8[1M], claude-sonnet-5, deepseek-v4, deepseek-v4-flash, dsv4 and kimi-k3.

- **Codex no longer reads the session model as "Custom", and no longer swaps it by itself.** The
  desktop overlay was aimed at "the first enabled model" of the active provider, which is rarely a
  model the catalogue offers: `~/.codex/config.toml` ended up naming `claude-3.6-flash`, Codex showed
  `Custom`, and the moment the offered list changed underneath it Codex switched the session by
  itself — "Model changed from Custom to claude-opus-4-7 (F)". The overlay, and every activation,
  now pick a model the catalogue actually offers (quickest verdict first), activation says when it
  had to substitute, and a model that is not offered is never written into the config. Measured on
  this machine: the config went from `claude-3.6-flash` (not offered) to `claude-opus-4-8[1M]`, which
  is in the list Codex reads — 20 models published, 19 of them tagged (F).

- **Sessions on a hub model can use tools again (the "unsupported call" / clock-only session).** The
  catalogue entry the hub serves to Codex carried
  `"experimental_supported_tools": ["send_user_message_async", "clock"]`. Codex reads that as the
  model's whole tool surface, so a chat on a hub model was left with a single clock tool — the model
  itself reported "my only callable tool here is a clock" while every real call came back
  *unsupported call*. The field must be present and **empty** (`[]`, the same value Codex's own entries
  use): dropping it entirely makes Codex fail to parse the entry and discard the hub's whole catalogue,
  which is what left the picker showing only the account's own models. Both halves are pinned by a test.
  Verified end to end with Codex's own resolver: `codex debug models` now returns 14 models, all 14 of
  them from the hub, each tagged `(F/N/S/U/X)`.

- **A rate-limited key now parks the whole provider instead of one model at a time.** Every probe
  spends the provider's key, and the user's own Codex session needs that same key: on 2026-09-11 the
  HHTech key went into a rate-limit cooldown and the desktop session answered *"Reconnecting 3/5"*
  with `Mã lỗi: 400-39a6d70a` while the background probes kept spending what was left. The pass now
  recognises the key's own message (`API key is rate limited`, `No usable API key remains`) in the
  provider's answer, stops immediately, and probes nothing for that provider for 10 minutes —
  observed in the log as `provider key is rate limited — no probes for 10 min`. The readiness check
  also carries a short piece of the provider's message into the verdict detail, which is what makes
  "the key is out" distinguishable from "this model is broken".
- **No key means no probe.** `CompatibilityService` used to probe with a null key while every key sat
  in cooldown, which answered 401 and stored a score-0 verdict against a perfectly good model —
  hiding it from the Codex catalogue for the full six-hour window. It now raises
  `ProviderKeyUnavailableException`, and the auto-ping parks the provider for 10 minutes instead of
  writing that verdict.

- **A background probe can no longer take the user's key away.** The gateway parked a key on any
  retryable upstream failure (429/5xx) — including failures of the hub's own measuring requests — so
  a probe that got throttled pushed the provider's key into a cooldown and the session the user was
  typing in answered *"Reconnecting 5/5 … No usable API key remains for provider 'HHTech API'"*
  (2026-09-11, 11:20). Probe requests now carry a marker header and leave key health to the user's own
  traffic. Verified by sending a probe-marked request at a model HHTech refuses with 503: the caller
  still sees the 503, and the key stays `Healthy`.

- **The background passes stopped measuring the image endpoints.** HHTech publishes ~60 image ids
  (`gpt-image-2*`, `gemini-*image*`, `grok-imagine-*`) beside its chat models; a Codex turn can never
  use one, yet every pass probed them, they answered 503, and that traffic is what pushed the HHTech
  key into the rate-limit cooldowns that broke the desktop session. Both passes now skip an id that
  names an image endpoint — measured after the change: zero probe lines for those ids (they used to be
  dozens per pass) while a full sweep ran, key still `Healthy`.
- **A parallel pass can no longer lose a "parked" mark.** The readiness pass probes two models at once
  and wrote its timeout / refusal / provider-pause maps through plain dictionaries; with two writers one
  entry was silently lost, so a model the provider had just refused was asked again on the next pass (and
  the key spent again). Both passes now use `ConcurrentDictionary`. A test caught this: ~1 run in 5 was
  red before the change, 0 in 8 after.

- **The hub now records what Codex asks its gateway for.** "The picker shows Codex's own models" and
  "Codex never asked us for ours" look identical from the outside, so the gateway logs each catalogue
  request (`models: Codex asked (client_version=…, ua="Codex Desktop/0.153.4 …") -> 14 model(s)`).
  That log is what showed the real fault: Codex *was* fetching the hub's catalogue and then discarding
  it (see the `experimental_supported_tools` entry below), so the picker fell back to the account's own
  models. With the entry parseable again the served catalogue replaces that fallback, and the model
  names stay clean — the `(F/N/S/U/X)` tag is the only marker the hub needs to add.

- **A provider-side error no longer takes the key away from the session.** One 503 from a provider used
  to mark the key `Offline` — and that mark sticks until the app restarts — so the next request in the
  user's own session met the gateway's `503 No usable API key remains`; Codex answered with
  `Reconnecting 5/5`, switched model twice and compacted context, which is the error chain the user saw
  on 2026-09-11 at 12:19. A 5xx is now handed straight back to Codex (which retries by itself) with the
  key left alone; a 429 fails over to another key when there is one and is handed back when there is
  not; a dropped connection answers 502 with the key intact. Only 401/402/429 (with a spare key) still
  say something about the key, because those are the answers that really are about the key. Live check:
  `glm-5.3-flash` and `gemini-3.1-pro` answered 503 while the HHTech key stayed `Healthy`, and three
  real turns (`claude-opus-4-8[1M]` 1.8 s, `gpt-5.6-sol` 2.5 s, `claude-v4-pro` 2.8 s) all returned 200.

- **The background passes stand down while the user is working.** They measure with the same key the
  session runs on, two models at a time and up to 45 s each, which is what a provider answers 429/503
  to — and those answers were what parked the key. Both passes now skip a provider for three minutes
  after any real traffic to it, and park themselves when the provider answers 429. A fresh verdict for a
  model nobody is asking about is worth less than the turn the user is waiting on. Four tests pin it,
  including that a catalogue read (`/v1/models`, i.e. Codex opening) does not count as user traffic.

- **A provider outage is no longer recorded as "these models are broken".** At 12:42 on 2026-09-11 a
  momentary DNS failure ("No such host is known … hhtechapi.com:443") was written as a score-0 verdict
  for every HHTech model, which set their state to `Failed`; the gateway only serves models whose state
  is `Enabled`, so it answered "Model … is not enabled for provider" to every request and the session
  fell apart while the user was switching models in the picker. Only a real answer about the model may
  change its verdict now — 429/5xx/DNS/connection/timeout belong to the provider
  (`ProviderTrouble.IsNotAMeasurement`, pinned by a theory test). The 16 models that were demoted this
  way were restored from their real, recent, good verdicts (backup kept next to the database).

- **Every turn is logged with the model Codex actually asked for.** One line per request
  (`responses: model=gpt-5.6-luna provider=hhtech -> 503 {…}`) so "I picked claude but it answered about
  luna" can be settled with evidence instead of guesswork — and provider error text is relayed and
  logged verbatim, which is how the real cause above was found.

- **The picker no longer offers image endpoints.** They are enabled and measurable, but no Codex turn
  can use one; they sat in the list marked `(U)` forever because the probes deliberately skip those ids.
  The catalogue went from 60 entries (48 of them image endpoints) to the 20 that can actually serve a
  turn.

- **The hub now says when Codex is stuck retrying an old turn.** Codex retries a failed turn with the
  model that turn started on, so on 2026-09-11 thirteen requests in sixteen seconds all asked for
  `gpt-5.6-luna` while the picker, the thread row and `config.toml` all said `claude-opus-4-7[1M]` — and
  the obvious conclusion ("the model I picked is not connected") was wrong but unanswerable from the UI.
  Three refusals of one model inside a minute now produce a line saying exactly that, with what to do
  about it. Pinned by a test.

- **A stuck turn is ended instead of retried forever.** Codex retries an unfinished turn with the model
  that turn started on — after a restart at 13:34 on 2026-09-11 it sent fifteen requests in twenty
  seconds all asking for `gpt-5.6-luna` while the picker, the thread row and `config.toml` said other
  models, and the provider refuses luna (all its upstream accounts busy). Because the hub relayed the
  provider's retryable 503, that turn never ended, so the model the user picked never got a turn at all
  — "Codex does not call the model I chose". Two refusals of the same model are handed back as they
  come; the third within a minute is answered **400 with the provider's own reason and what to do**
  (`adam_codexhub_model_unavailable`), which ends the turn and lets the next one run on the picked
  model. The counter resets afterwards, so a later attempt still gets the provider's real answer.
  Measured live: `gpt-5.6-luna` answered 503, 503, 400, 503, 503 while `claude-opus-4-8[1M]` and
  `claude-opus-4-7[1M]` answered 200 in the same run.

- **A stuck turn is served instead of retried forever.** Codex re-runs an unfinished turn with the model
  that turn started on, and it ignores non-retryable answers: after the 400 above, the hub still counted
  `gpt-5.6-luna` thirteen more times in eleven seconds, because the turn had not ended. The only way out
  is for the request to succeed, so a model the provider keeps refusing (3 refusals in a minute) is
  served, **for that turn**, by the fastest model of the same provider with a known-good verdict. It is
  never silent: a log line names both models and `x-adam-codexhub-fallback` carries the one that
  answered. The counter goes quiet after a minute without refusals, so a later attempt still gets the
  provider's own answer. Measured live on 2026-09-11 13:46: `gpt-5.6-luna` → 503, 503, 400, then **200
  served by `claude-opus-4-7`** (twice), while `claude-opus-4-7[1M]` answered 200 in the same run.

- **Switching the model in Codex now carries the work on, instead of breaking it.** Codex keeps an
  unfinished turn on the model that turn started with, so picking another model in its picker changes
  nothing about the requests already in flight — the session kept asking for the old model and failing,
  which reads as "the app refuses to work when I change model". The hub reads the model Codex is set to
  (from Codex's own session state, the only place its picker writes) and, as soon as the requested model
  is refused once, continues that turn with the model the user picked — logged and named in
  `x-adam-codexhub-fallback`. The generic "whichever model answers" fallback still waits for a run of
  refusals, and the plain "cannot serve this request" answer is only used when there is nothing left to
  continue with. Measured live on 2026-09-11 14:08: the session kept asking for `gpt-5.6-luna` (503),
  and the next attempt was served by `claude-opus-4-7[1M]` — the model picked in Codex — with 200.

- **The tray says when a turn is being served by a different model.** A turn Codex keeps on a model that
  stopped answering is continued on another model (usually the one the user just picked), and while that
  is happening the tray menu carries a line under the session model —
  `Đang chạy lượt này bằng claude-opus-4-7[1M] (thay cho gpt-5.6-luna)` — plus the same text in the tray
  tooltip, so a continued turn never looks like a silent one. It appears only while it is true.

## v1.5.3

### Fixed

- **Switching back to Codex Account no longer leaves the gateway's model behind.** Before overlaying
  `~/.codex/config.toml`, the hub snapshots the native config into `config-ACCOUNT.toml` so the Codex
  Account card can put it back. That snapshot was also taken while the config still carried the
  gateway overlay — and with it the upstream `model` id the overlay had written (on the HHTech run
  `model = "claude-5.5"`). Restoring the account then handed that id back to the ChatGPT sign-in, so
  Codex Desktop came up signed in to the account while still listing the gateway's models, and the
  tray reported `Codex đang dùng: claude-5.5` for a session that was not using it at all.
  A config that still carries the gateway overlay is no longer snapshotted as the account profile,
  and when a profile does carry one, its `model` line is now dropped together with the provider
  block — Codex falls back to the account's own default model.
- **Threads are no longer left stranded on the previous provider's model.** Codex pins a model per
  thread (`threads.model` in `~/.codex/state_5.sqlite`), so every thread kept the id it was last
  used with. After a gateway run, 43 threads were still pinned to HHTech ids (`claude-5.5`, `dsv4`,
  `deepseek-v4-flash`, …), and Codex refused every turn in them with *"The 'claude-5.5' model is not
  supported when using Codex with a ChatGPT account"* while the picker fell back to `Custom`.
  Activating a provider now moves every thread whose model that provider does not offer onto the
  model being activated — Codex Account resolves the catalogue from Codex's own model cache and
  prefers `gpt-5.6-sol`. The rewrite only runs while Codex is closed, only ever touches the `model`
  column (never a transcript), keeps `codex-auto-review` intact, and reports the count it moved in
  the activation message.
- Data repair for machines already in that state: the leftover `model` line is a one-line fix in
  `~/.codex/config.toml` and `~/.codex/config-ACCOUNT.toml` (pre-repair copies under
  `~/.codex/adam-codexhub-backups/`), and the two errors above can be cleared in place with the
  thread migration — both were applied on this machine on 2026-09-10 (43 threads → `gpt-5.6-sol`).

## v1.5.2

### Fixed

- **A fresh launch no longer shows a wall of "not checked yet".** The colour picture came from a
  background refresher that waited 90 s and then re-tested only **3 models per 15 minutes** — on a
  14-model provider the list stayed half grey for over an hour. The first tick now starts ~5 s
  after launch and sweeps the **whole** enabled list of the active provider in one pass (up to 24
  models); only the ticks after it fall back to the small incremental batch. Anything already
  answered by the stored results is left alone, so the models that were good last run keep their
  colours and are not re-tested at all.
- the tray status line, the startup log line and `ModelAutoPingService` now agree: the log reads
  `opening sweep of up to 24 model(s), then every 15 min, up to 3 model(s) per tick`.

### Changed

- **The status letters moved off the tray and into Codex.** The tray already colours every model, so
  the one-letter tags added in v1.5.1 were dropped there — and the legend line with them. Codex
  Desktop's picker cannot be coloured at all, so that is where the letters stayed: the hub appends
  `(F)` / `(N)` / `(S)` / `(U)` to the `display_name` of every model in the app-server catalogue it
  serves, plus a one-line legend in the entry description.
- **Every model list is indexed fastest first.** The colour groups are unchanged, but inside a group
  the model with the smallest measured first byte now leads (total time as the tiebreak, name last)
  and models with no measurement sink to the bottom. The tray submenu, the in-app selectors and the
  catalogue Codex reads share one order, and the catalogue's `priority` numbering follows it.

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
