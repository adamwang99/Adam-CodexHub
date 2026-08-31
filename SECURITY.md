# Security Policy

Do not publish API keys, authorization headers, prompt contents or private source code.

Adam CodexHub must:

- protect provider keys using Windows user-bound encryption
- redact credentials from logs
- bind the gateway to `127.0.0.1`
- avoid prompt/response body logging by default
- preserve last-known-good Codex configuration
