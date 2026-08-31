# 07 — Model Discovery & Compatibility

## 1. Discovery is not verification

A model returned by `/models` is only `Discovered`.

It must not be assumed Codex-compatible.

## 2. Model states

```text
Discovered
Testing
Verified
Enabled
Disabled
Unavailable
Deprecated
Failed
```

## 3. Discovery sources

- provider model endpoint
- registry metadata
- manually entered model name
- local runtime model list

## 4. Capability probes

Compatibility engine may test:

- text generation
- Responses API
- chat completions
- streaming
- tool/function calling
- structured JSON
- image input
- reasoning parameters
- long context
- cancellation
- parallel tool calls

## 5. Compatibility score

Use score only as an explanatory aid.

Never imply scientific precision.

Example:

```text
96 / 100
```

with visible capability matrix.

## 6. Enable flow

```text
Discovered
  ->
Test
  ->
Verified
  ->
User enables
  ->
Codex catalog
```

## 7. New model detection

Compare provider snapshots.

Changes:

```text
NEW
UPDATED
REMOVED
RETURNED
UNCHANGED
```

A missing model should become `Unavailable`, not immediately deleted.

## 8. Auto-update

Recommended setting:

```text
Scan enabled providers every 24 hours
```

Allow:

- startup check
- daily
- weekly
- manual only

Do not spam providers with frequent scans.

## 9. Model picker hygiene

Only enabled models go to Codex.

Do not display hundreds of discovered models in the primary picker.
