# 10 — Data Model

## 1. Main entities

### Provider
```text
Id
Name
AdapterType
BaseUrl
TrustLevel
Enabled
Health
CreatedAt
UpdatedAt
```

### ProviderKey
```text
Id
ProviderId
Label
SecretReference
Priority
Enabled
Health
CooldownUntil
LastTestAt
LastSuccessAt
LastFailureAt
FailureReason
```

### Model
```text
Id
ProviderId
RemoteId
DisplayName
Enabled
DiscoveryState
CompatibilityState
InputModalities
Capabilities
ContextWindow
LastSeenAt
LastVerifiedAt
```

### SessionBinding
```text
Id
ProjectId
ExternalSessionIdNullable
ProviderId
ModelIdNullable
CreatedAt
LastUsedAt
LastSeenProjectRevision
Status
```

### Project
```text
Id
Path
Name
CurrentRevision
LastScanAt
```

### ProjectRevision
```text
ProjectId
Revision
CreatedAt
ChangedFilesSummary
GitHead
StateHash
```

### CompatibilityResult
```text
ProviderId
ModelId
Probe
Status
Latency
VerifiedAt
DetailsJson
```

### UsageRecord
```text
ProviderId
ModelId
KeyAlias
Timestamp
InputTokensNullable
OutputTokensNullable
EstimatedCostNullable
Latency
StatusCode
```

## 2. Secret separation

`ProviderKey.SecretReference` points to secure Windows storage.

The database never contains the full API key.
