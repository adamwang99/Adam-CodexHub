# 08 — Session Continuity Engine

## 1. Core rule

> A Codex session keeps affinity with the provider that created it.

Do not silently rewrite provider identity underneath an existing session.

## 2. Why

Different providers may differ in:

- tool schemas
- Responses behavior
- streaming
- reasoning fields
- image support
- context behavior
- thread metadata

Direct switching may produce session resume failures or stale assumptions.

## 3. Shared project state

Use:

```text
<project>\.adam-codexhub\
  CURRENT_STATE.md
  project-state.json
  session-index.json
  handoffs\
```

## 4. Source of truth priority

```text
1. Current filesystem
2. Git state
3. Shared project state
4. Current project instructions
5. Old chat history
```

If old chat memory conflicts with current project state, current state wins.

## 5. Project revision

Maintain a monotonic project-state revision.

Example:

```text
Current revision: 194
DeepSeek session last seen: 181
```

DeepSeek session is stale by 13 revisions.

## 6. Switching providers

### DeepSeek -> TTMAPI

```text
Save current state
  ->
Update revision
  ->
Find TTMAPI session
  ->
If found: stale check + refresh
If not found: new session + handoff
```

## 7. Returning to old provider

Default:

```text
Resume + Auto Sync
```

Not:

```text
always create new session
```

## 8. Sync levels

### Light
- current state
- revision
- recent decisions

### Normal
- current state
- git status
- changed files
- pending tasks

### Full
- current state
- git status
- git diff
- recent commits
- changed files
- build/test state
- project instructions

Default:

**Smart Auto**

## 9. Smart Auto policy

No meaningful changes:

`resume immediately`

Small change:

`light`

Normal work change:

`normal`

Large structural change / old session:

`full`

## 10. New session handoff

A new session should not start from zero.

Provide:

- current objective
- completed work
- pending work
- important decisions
- changed files
- known issues
- next recommended action
- previous provider/model
- current revision

## 11. Stale assumption correction

When resuming an old session, include explicit correction:

```text
IMPORTANT STATE UPDATE

Some assumptions from this chat may be outdated.
Current filesystem and CURRENT_STATE.md take priority.

Review the synchronized state before continuing.
```

## 12. Session switching UI

Default setting:

```text
Automatically save work state before switching       ON
Automatically update stale sessions                  ON
Resume previous session when returning to provider   ON
Inspect Git changes before resume                    ON
Warn on heavily outdated session                     ON

Resume behavior:
● Resume when safe
○ Always new
○ Ask every time
```

## 13. Important implementation constraint

Do not depend on undocumented mutation of Codex internal chat databases.

If exact automatic thread opening is unavailable, Adam CodexHub still:

- prepares handoff
- activates provider
- opens Codex
- presents/copies continuation instruction
