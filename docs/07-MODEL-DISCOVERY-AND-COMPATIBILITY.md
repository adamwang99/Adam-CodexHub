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

## 10. Latency-aware status

A model that responds is not necessarily a model that is comfortable to use.

Store the measured latency next to every compatibility result:

```text
first_byte_ms
total_ms
```

Classify each model for the pickers:

```text
Callable   green   text + Responses + streaming work, first byte within 15 s
Slow       amber   everything works, but first byte or total above 15 s
Unknown    grey    never probed, or the stored result is older than 6 h
Skip       red     a required capability failed (hidden unless "show all")
```

Model pickers colour the model name itself and order models `Callable -> Slow -> Unknown -> Skip`, alphabetical inside a group, without changing which model is selected.

A bounded background re-check may refresh at most 3 models of the **active** provider every 15 minutes. Never two checks at once, never a model with a manual test in flight, and never a provider the user has not activated.
