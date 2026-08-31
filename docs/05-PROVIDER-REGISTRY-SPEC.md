# 05 — Provider Registry Specification

## 1. Goal

Provider support must be data-driven and expandable without rebuilding the entire application.

## 2. Registry layers

```text
Built-in
Verified online registry
Community
Custom user-defined
Local detected
```

Trust levels:

```text
OFFICIAL
VERIFIED
COMMUNITY
EXPERIMENTAL
CUSTOM
```

## 3. Provider definition

See:

`schemas/provider.schema.json`

A provider definition can describe:

- id
- display name
- base URL
- adapter type
- authentication
- models endpoint
- responses endpoint
- chat endpoint
- special headers
- capabilities
- model filters
- documentation URL
- trust level

## 4. Registry update

Registry should be independently versioned from application binaries.

Example:

```text
registry version 18 -> 19
```

App downloads only changed definitions when possible.

## 5. User-added providers

Minimum UI:

```text
Name
Base URL
API Key
```

Auto Detect probes:

- authentication
- models endpoint
- responses endpoint
- chat-completions endpoint
- streaming
- tool calls
- basic vision where appropriate

The resulting provider definition is shown for approval.

## 6. Import/export

Provider definition export must not include API keys.

Recommended extension:

```text
.codexhub-provider.json
```

## 7. Community safety

Community definitions must never:

- embed credentials
- execute scripts
- specify arbitrary local commands
- bypass TLS by default

Registry files are declarative data only.
