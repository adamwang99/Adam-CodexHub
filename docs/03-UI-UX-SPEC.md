# 03 — UI / UX Specification

## 1. Design objective

The interface should look like a professional Windows control utility:

- compact
- dark
- fast
- low visual noise
- clear status
- minimal modal interruptions
- advanced controls hidden until needed

Avoid dashboard overload.

## 2. Main window layout

Recommended default size:

```text
1180 x 760
```

Minimum:

```text
980 x 640
```

Layout:

```text
┌───────────────────────────────────────────────────────────┐
│ Top Bar: Logo | Active Provider | Model | Health | Search│
├──────────────┬────────────────────────────────────────────┤
│ Sidebar      │ Main Content                               │
│              │                                            │
│ Home         │                                            │
│ Providers    │                                            │
│ API Keys     │                                            │
│ Models       │                                            │
│ Sessions     │                                            │
│ Usage        │                                            │
│ Diagnostics  │                                            │
│ Settings     │                                            │
├──────────────┴────────────────────────────────────────────┤
│ Status Bar: Gateway | Codex | Registry | Last Sync        │
└───────────────────────────────────────────────────────────┘
```

## 3. Top bar

Left:

- compact Adam CodexHub logo/wordmark

Center:

- active provider selector
- active model selector

Right:

- provider health pill
- gateway status
- settings shortcut

Example:

```text
[DeepSeek ▼] [V4 Pro ▼]   ● Healthy   Gateway ●
```

## 4. Home screen

Home should answer only:

- What is active?
- Is it healthy?
- What session am I in?
- Is project state current?
- What can I switch to?

Recommended cards:

### Current Provider
Provider, model, active key masked suffix.

### Session
Provider-affinity state, project revision, stale/current indicator.

### Quick Switch
Recent providers.

### System Health
Gateway, Codex config, registry.

Do not show large analytics by default.

## 5. Providers screen

Use master-detail layout.

Left list:

```text
DeepSeek       Healthy
Qwen           Healthy
OpenRouter     Warning
Codex Account  Ready
Custom API     Offline
```

Right panel tabs:

- Overview
- Connection
- Models
- Keys
- Compatibility
- Advanced

Actions:

- Activate
- Test
- Scan Models
- Clone
- Export Definition
- Disable
- Delete

## 6. Add Provider wizard

Step 1:

```text
Choose:
[Preset]
[Auto Detect]
[Custom]
[Local]
```

Auto Detect form:

```text
Provider Name
Base URL
API Key
[Auto Detect]
```

Show progress:

```text
✓ Authentication
✓ /models
✓ /responses
✓ /chat/completions
✓ Streaming
? Vision
```

Then:

```text
Detected profile
[Review] [Add Provider]
```

## 7. API Keys screen

Each provider has a key pool.

Row:

```text
≡ Key A   ****8F2C   Priority 1   Healthy      42ms  [Test]
≡ Key B   ****17D1   Priority 2   Cooldown     00:41
≡ Key C   ****9A20   Priority 3   Quota Empty
```

Support drag-and-drop priority.

Bulk actions:

- Add Key
- Paste Many
- Test All
- Enable All
- Disable Failed
- Export key labels only

Never reveal full keys after save.

## 8. Models screen

Two-pane model view.

Filters:

- Enabled
- New
- Verified
- Vision
- Tool Calling
- Reasoning
- Unavailable

Model card:

```text
DeepSeek V4 Pro
Verified
Text ✓  Tools ✓  Streaming ✓  Vision ✗
Compatibility 96
Last verified 4h ago

[Enabled toggle] [Retest]
```

Newly discovered models must show a `NEW` badge.

## 9. Sessions screen

Show session/provider affinity.

Example:

```text
Adam Video Studio
  DeepSeek / V4 Pro
  Last used 18 min ago
  Revision seen: 183
  Current revision: 191
  Status: STALE
  [Resume + Sync]
```

Actions:

- Resume + Sync
- Full Refresh
- Start Fresh + Handoff
- Open without Sync (advanced warning)

## 10. Provider switching UX

When switching provider, use one compact decision sheet.

If target provider already has a session:

```text
Switch to TTMAPI

Existing TTMAPI session found.
Project changed by 7 revisions.

● Resume + Auto Sync   Recommended
○ Start Fresh + Handoff
○ Switch provider only (Advanced)

[Continue]
```

If no session exists:

```text
No TTMAPI session exists for this project.

● Create session + import current state
```

## 11. First-run acknowledgement modal

Cannot close via X.

Title:

**Important: How Provider Switching Works**

Required checkbox.

Continue disabled until checked.

Include link:

`Read how session switching works`

## 12. Notifications

Use toast/banners for routine events.

Examples:

```text
✓ Switched DeepSeek key #1 -> #2
```

```text
3 new models discovered
[Review]
```

```text
Session refreshed from project revision 181 -> 194
```

Avoid blocking dialogs except:

- destructive delete
- secret export
- unsafe session operation
- first-run acknowledgement

## 13. Accessibility

- keyboard navigation
- clear focus states
- do not communicate status by color alone
- scalable fonts
- minimum body text 13–14 px
- status icon + text

## 14. Visual density

Use 8px base spacing.

Common:

- card radius: 8–10px
- control height: 34–38px
- sidebar row: 40px
- card padding: 16px
- section spacing: 24px

The UI should be dense enough for technical users but never resemble an enterprise admin portal.
