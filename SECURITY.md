# Security Policy

## Supported versions

| Version | Security support |
| --- | --- |
| Latest `0.1.x` release | Supported |
| Older builds and source snapshots | Not supported |

Early releases are unsigned. Verify the SHA-256 file published beside the ZIP and download only from the official repository.

## Report privately

Do not place API keys, authorization headers, prompt contents, personal data or private source code in a public issue.

Use [GitHub Private Vulnerability Reporting](https://github.com/adamwang99/Adam-CodexHub/security/advisories/new) for vulnerabilities. Include the affected version, impact, reproduction steps and a minimal redacted proof of concept. If private reporting is temporarily unavailable, contact the repository owner through the private contact method shown on the [maintainer profile](https://github.com/adamwang99) and ask for a secure reporting channel before sending details.

The maintainer aims to acknowledge a complete report within three business days and provide an initial assessment within ten business days. These are best-effort targets for a free community project, not guaranteed service levels.

## Security boundaries

Adam CodexHub is designed to:

- protect provider keys at rest with Windows user-bound DPAPI encryption;
- avoid logging authorization headers, secret values, prompts and responses;
- bind the gateway to `127.0.0.1` only;
- generate a new cryptographically random gateway token each time the gateway starts;
- require HTTPS for remote providers and permit HTTP only for loopback endpoints;
- preserve last-known-good Codex configuration and support rollback.

These controls do not protect against malicious software running as the same Windows user, a compromised provider account, unsafe upstream handling, or a compromised custom endpoint. Treat local project handoff files and diagnostic logs as potentially sensitive metadata.

## Coordinated disclosure

Allow reasonable time for investigation and a release before publishing exploit details. The maintainer will credit reporters who request attribution, subject to safety and privacy constraints.
