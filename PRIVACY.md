# Privacy Notice

Last updated: August 31, 2026

Adam CodexHub is a local-first, independently maintained open-source application. This notice explains what the application stores locally, when data leaves the device, and what data the maintainer may receive through project support channels.

This notice is general information, not legal advice or a promise that every third-party provider follows the same practices.

## 1. Application operation

Adam CodexHub does not require an Adam account and does not operate an Adam-hosted backend for prompts, API keys, model responses or project files. The application has no built-in advertising, payment processing, analytics or remote telemetry.

The application stores the following data locally for its operation:

- provider profiles, model metadata and compatibility results;
- API keys encrypted with Windows Data Protection API (DPAPI) for the current Windows user;
- application settings and acknowledgement records;
- Codex configuration backups and recovery state;
- project handoff files under `.adam-codexhub` when the user enables project continuity;
- limited local diagnostic information, such as startup errors and file paths.

Prompt and response bodies are not intentionally logged by default.

## 2. Remote AI providers

When a user selects a remote provider, Codex and Adam CodexHub may transmit prompts, source code, files, outputs, model identifiers and technical metadata to that provider or endpoint. Key tests, model discovery, compatibility probes, retries and failover also make real network requests.

The selected provider determines its own collection, retention, training, security, regional processing and international-transfer practices. Some routing services may send data to an additional model operator. Adam CodexHub does not control those practices.

Review [Provider Data Disclosures](docs/PROVIDER-DATA-DISCLOSURES.md) and the provider's current terms and privacy documentation before use. Do not send personal, confidential, proprietary or regulated information unless you are authorized to do so and have confirmed that the provider and account terms are appropriate.

Local providers such as Ollama or LM Studio can keep inference traffic on the device when configured only for local operation. Their installers, model downloads, extensions or optional online services may still make separate network requests.

## 3. Support and repository services

The maintainer may receive information that a user voluntarily submits through GitHub issues, discussions, pull requests, private vulnerability reports or other contact channels. This can include a GitHub identity, contact information, logs, screenshots and issue details.

Do not submit API keys, authorization headers, private prompts, private source code or unnecessary personal data. Security reports should use the private process in [SECURITY.md](SECURITY.md).

GitHub and any other communication platform process information under their own terms and privacy policies. Release download statistics shown by GitHub are provided by GitHub; the application does not send download analytics to the maintainer.

## 4. Roles and legal responsibility

For ordinary local application use, the maintainer does not receive or determine the purpose of prompts sent directly to a provider selected by the user. The user and provider are responsible for determining their respective privacy roles under applicable law.

The maintainer may act as a controller or similar responsible party for support information actually received and used to answer a report, secure the project or maintain the repository.

Where the GDPR or similar law applies to that support information, processing is generally based on the maintainer's legitimate interests in responding to requests, maintaining the project and protecting users, or on compliance with legal obligations. Information is not sold by the application or used for cross-context behavioral advertising.

Organizations using Adam CodexHub remain responsible for lawful basis, notices, data-processing agreements, confidentiality obligations, data-subject rights, international transfers and sector-specific requirements.

Depending on applicable law, a person may request access, correction, deletion, restriction, portability or objection concerning support information actually held by the maintainer, and may complain to an appropriate privacy regulator. These rights can be limited by repository integrity, security, freedom-of-expression and legal-record requirements. Submit a request privately through the contact route in Section 8 and identify the relevant support interaction. The project has no dedicated data protection officer.

## 5. Retention and deletion

Local application data remains until the user removes it. Before deletion, stop Adam CodexHub and preserve any recovery files still needed.

Typical locations are:

- `%LOCALAPPDATA%\AdamCodexHub\` for the database, settings, encrypted keys and logs;
- `%USERPROFILE%\.codex\adam-codexhub-backups\` for Codex configuration backups;
- `<project>\.adam-codexhub\` for project handoff state.

Deleting local Adam CodexHub data does not delete information already sent to a provider. Use the provider's account and privacy processes for those requests.

Support records may be retained while needed to resolve the request, protect the project, meet legal obligations and preserve the public development history. Public GitHub contributions normally remain part of repository history.

## 6. Security limitations

DPAPI reduces exposure of keys stored at rest, but it does not protect against malicious software running as the same Windows user. A loopback-only gateway reduces network exposure, but another process running under the same user may still access local files or attempt local requests.

Use a supported Windows system, protect the Windows account, keep provider keys scoped and revocable, review provider usage, and install releases only after verifying their origin and checksum.

## 7. Children and high-risk data

Adam CodexHub is a developer tool and is not directed to children. Provider age and eligibility requirements still apply. The application is not certified for medical, legal, financial, employment, education admissions, law-enforcement, critical-infrastructure or other regulated or high-risk processing.

## 8. Changes and contact

Material changes will update this file and its date. Privacy questions can be directed to the repository owner through the contact methods published on the [maintainer's GitHub profile](https://github.com/adamwang99). Do not place private information in a public issue.
