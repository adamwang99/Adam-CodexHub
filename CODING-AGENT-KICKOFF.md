# Coding Agent Kickoff

You are implementing Adam CodexHub.

First, read `AGENTS.md`, `PROJECT-CONTEXT.md`, and all documents referenced there.

Do not redesign the architecture before building the existing skeleton.

## First objective

1. Restore NuGet packages.
2. Build the entire solution on Windows.
3. Run tests.
4. Fix compile-time/API drift only.
5. Record every change in `CHANGELOG.md`.
6. Do not start gateway forwarding until the baseline solution builds cleanly.

## After baseline build

Implement V1 in this order:

1. provider/profile SQLite persistence
2. key pool health/test/reorder
3. safe Codex config merger preserving legacy provider blocks
4. provider auto-detect wizard
5. model scan persistence
6. real compatibility probes
7. gateway request forwarding and streaming
8. provider switch planning UI
9. session stale-state sync UI
10. diagnostics and restore-last-working-config

All security and session-affinity rules are mandatory.
