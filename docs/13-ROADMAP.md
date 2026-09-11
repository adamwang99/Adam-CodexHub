# 13 — Roadmap

Rewritten 2026-09-11. The previous version had drifted badly: it listed "V1.5 — Registry" as the
current milestone while the shipped v1.5.x releases were about something else entirely (a live model
catalogue, provider recovery, and making a provider switch reach the chat already on screen), and the
registry work had not started. A roadmap that neither records what shipped nor predicts what is next
is worse than none, because it gets used for decisions.

Rule for keeping it honest: **a milestone moves to "Shipped" only when the release exists**, and
anything discovered mid-flight gets written down here rather than remembered.

## Shipped

### V1 — Stable provider control
WPF shell · Codex account profile · provider presets · custom providers · DPAPI key storage ·
multi-key pool · key tests · local gateway · model scan · model enable/disable · atomic Codex config
writes · backup/restore · session affinity metadata · first-run acknowledgement · basic project state
sync · diagnostics.

### V1.1 — Continuity and verification
Real compatibility probes · stale-session detection · auto refresh · hand-off generation · provider
health history · model change detection · usage metadata.

### V1.5.x — Making the switch actually work
This is where most of the real engineering went, and none of it was on the old plan:

- **Codex-readiness gate** — a model reaches Codex's picker only after it answers a real request, so
  the list stops offering models that fail on first use.
- **Speed-graded catalogue** — measured latency colours every model name and orders every list
  fastest-first; Codex's own picker gets `(F)/(N)/(S)/(U)/(X)` tags, the one surface the hub cannot
  colour.
- **Provider outages stop poisoning verdicts** — a DNS failure once recorded 60 models as `Failed`
  and emptied the picker; network trouble is now never a judgement about a model.
- **Key arbitration** — background probes yield to a live session, a rate-limited key parks the whole
  provider instead of one model at a time, and a probe never marks the user's key unhealthy.
- **Stuck turns finish** — Codex pins a model per turn, so a refused model used to loop forever; the
  hub now continues the turn with the model the user picked and says so in the tray.
- **A provider switch reaches the open chat** — per-thread model pins are repaired at startup and on
  activation (with a backup and a busy timeout), and the hand-off no longer forces a new chat.
- **Identity prompt neutralised** — the client's "You are Codex, an agent based on GPT-6" claim made
  models from other families refuse the work; the gateway replaces it with an honest harness framing.
- **Model catalogue self-refreshes** and a provider that ran out of quota recovers on its own.
- **Release pipeline** — one tag publishes installer + portable + checksums.

## Next

Ordered by value per unit of work, not by when it was first imagined.

### N1 — Finish the permission story (biggest open gap)
A chat's tool surface is decided by Codex, not by the hub: measured 2026-09-11, a new thread carried 8
tools while an older one carried none, and writing `sandbox_policy` straight into `state_5.sqlite` is
overwritten by Codex on open. Adam's requirement — *one chat, any provider or model, full tools* — is
therefore **not met** and must not be claimed. Work: find the supported path that makes an existing
thread carry tools, or detect the state and tell the user plainly in the UI.

### N2 — Self-service diagnosis
A "export a diagnostic bundle" button (logs + versions + provider/key health, secrets redacted) so a
bug report is one file instead of a conversation. The release check shipped as part of this idea; the
bundle did not.

### N3 — Provider health scoring
Key health and per-model callability exist; what is missing is history per provider (latency, refusal
and error rates over time) so "which provider is flaky today" is answerable with a number.

### N4 — Auto Router
The ingredients are already in place — measured speed grades, key pool, failover, verdict store.
Combining them into "run this turn on the cheapest healthy model that can do it" is the natural next
product step, and the reason to build N3 first.

### N5 — Registry (carried over from the old V1.5)
Presets are compiled into the build today, so a new provider needs a release. A downloadable registry
with provider definitions, trust levels and import/export removes that coupling. Still worth doing —
just not before N1–N3.

## Later

- Model aliases and task-aware model suggestions (needs N3's data to be more than a guess).
- Cost comparison across providers.
- Plugin adapter model.
- macOS/Linux UI reconsideration.
- Optional cloud sync of non-secret settings.

## Explicitly out of scope

- **Browser automation of subscription web chats.** That is a separate project (Adam BrainHarness)
  with a different technology, cost model and risk profile. It must never be mixed into this codebase.
- **Silent auto-update.** The build is unsigned and 90–130 MB; the hub checks for a release when asked
  and leaves installing to the user.
- **Modifying Codex Desktop itself.** It is a closed, self-updating MSIX package.
