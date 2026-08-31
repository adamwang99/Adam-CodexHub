# 06 — API Key Pool & Failover

## 1. Key model

Each provider may have zero, one or many API keys.

Stored fields:

- key id
- provider id
- user label
- secret reference
- priority
- enabled
- health
- last test
- last success
- last failure
- cooldown until
- failure category

## 2. Secret handling

Never store full plaintext key in SQLite.

Use:

- Windows Credential Manager, or
- DPAPI-protected secret blob

UI only shows masked value:

```text
****8F2C
```

## 3. Health states

```text
Healthy
Unknown
RateLimited
Cooldown
QuotaEmpty
Unauthorized
Disabled
Offline
```

## 4. Failover policy

Suggested behavior:

### 401 / invalid authentication
Mark key `Unauthorized`.

Do not use until user updates it.

### 402 / insufficient quota
Mark `QuotaEmpty`.

Move to next eligible key.

### 429
Mark `Cooldown`.

Respect retry-after when available.

### 5xx
Temporary provider failure.

Retry according to bounded policy.

## 5. Key ordering

Support:

- drag-and-drop
- explicit priority integer
- optional round-robin among same-priority keys

V1 default:

**priority first**

## 6. Testing

Single test:

`Test Key`

Bulk:

`Test All`

Test should distinguish:

- authentication success
- model-list success
- inference success

## 7. User messaging

Routine failover should be non-blocking:

```text
DeepSeek key #1 exhausted.
Switched to key #2.
```

Only interrupt if no usable keys remain.
