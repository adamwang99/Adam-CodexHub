# 11 — First Run & User Guide

## 1. First-run sequence

### Screen 1 — Welcome

```text
Adam CodexHub
Connect Codex to the providers you choose.
```

### Screen 2 — Mandatory session explanation

Title:

**Important: How Provider Switching Works**

Text:

Adam CodexHub lets Codex work with multiple AI providers. To avoid broken sessions, missing tools, provider mismatch and stale context, the app does not directly convert an existing chat session from one provider into another.

When you switch providers:

- your current work state is saved automatically
- an existing session for the target provider is resumed when possible
- otherwise a new session is created with the latest project state
- when returning to an older session, stale project information is synchronized automatically
- API-key rotation inside the same provider can occur without creating a new session

Source of truth:

> Current project files, Git state and synchronized project state take priority over old chat history.

Required checkbox:

```text
[ ] I understand that each provider may use a separate session
    and that Adam CodexHub may automatically synchronize or
    refresh project state when switching or resuming sessions.
```

Continue remains disabled until checked.

Do not provide a close X.

### Screen 3 — Choose initial setup

```text
Use Codex Account Login
Add an API Provider
Detect Local Provider
Skip for now
```

## 2. Common workflows

### Switch provider

User selects target provider.

Adam CodexHub:

1. snapshots work state
2. updates project revision
3. activates target provider
4. resumes or creates target session
5. synchronizes state
6. continues

### Return to previous provider

Adam CodexHub resumes the last compatible session and updates stale state automatically.

### Key exhausted

No session change is required.

Adam CodexHub moves to the next healthy key.

## 3. Recommended defaults

```text
Auto-save before provider switch      ON
Auto-refresh stale sessions           ON
Resume old provider session           ON
Smart Auto sync                       ON
Automatic key failover                ON
Model auto-enable                      OFF
Prompt/response logging               OFF
```
