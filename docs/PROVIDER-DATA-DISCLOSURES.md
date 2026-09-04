# Provider Data Disclosures

Last reviewed: August 31, 2026

Adam CodexHub does not operate the listed AI services. Provider presets contain connection metadata only and do not establish endorsement, privacy suitability, contractual acceptance or continuing compatibility.

## What may be transmitted

Depending on the Codex request and provider capabilities, a remote provider may receive:

- prompts, conversation context and generated outputs;
- source code, file contents, tool arguments and tool results;
- model identifiers, request options and technical metadata;
- account, organization and API-key identifiers visible to that provider.

Model discovery, API-key tests and compatibility tests send real requests. The current compatibility suite can request text, tool calling and structured JSON. Gateway retries or same-provider key failover may repeat a request and may create additional billable work.

## Provider resources

The links below are starting points. Providers can change URLs, terms, subprocessors, retention, training controls and regional endpoints at any time. Review the documents shown in the user's account and purchasing flow before activation.

| Preset | Terms | Privacy | Important consideration |
| --- | --- | --- | --- |
| Codex Account / OpenAI | [Terms](https://openai.com/policies/terms-of-use/) | [Privacy](https://openai.com/policies/privacy-policy/) | Native account behavior and API behavior may have different product terms. |
| OpenRouter | [Terms](https://openrouter.ai/terms) | [Privacy](https://openrouter.ai/privacy) | Requests can be routed to additional model providers with different retention and training practices. |
| DeepSeek | [Terms](https://cdn.deepseek.com/policies/en-US/deepseek-terms-of-use.html) | [Privacy](https://cdn.deepseek.com/policies/en-US/deepseek-privacy-policy.html) | Confirm service region, transfer location and account-level data controls. |
| Groq | [Terms](https://groq.com/terms-of-use/) | [Privacy](https://groq.com/privacy-policy/) | Confirm API data-use and retention settings for the selected account tier. |
| Mistral AI | [Terms](https://mistral.ai/terms-of-use) | [Privacy](https://mistral.ai/terms#privacy-policy) | Review API terms separately from consumer chat products. |
| Together AI | [Terms](https://www.together.ai/terms-of-service) | [Privacy](https://www.together.ai/privacy) | Hosted and dedicated offerings can have different data controls. |
| Fireworks AI | [Terms](https://fireworks.ai/terms-of-service) | [Privacy](https://fireworks.ai/privacy-policy) | Review serverless and dedicated deployment retention separately. |
| xAI | [Terms](https://x.ai/legal/terms-of-service) | [Privacy](https://x.ai/legal/privacy-policy) | Confirm that the legal documents apply to the API product and selected region. |
| Qwen / Alibaba Cloud DashScope | [Terms](https://www.alibabacloud.com/help/en/legal/latest/alibaba-cloud-international-website-product-terms-of-service) | [Privacy](https://www.alibabacloud.com/help/en/legal/latest/alibaba-cloud-international-website-privacy-policy) | The preset uses an international endpoint; regional products and rules can differ. |
| Ollama Local | [Terms](https://ollama.com/terms) | [Privacy](https://ollama.com/privacy) | Inference can remain local, but downloads and optional online services may contact Ollama. |
| LM Studio Local | [Terms](https://lmstudio.ai/terms) | [Privacy](https://lmstudio.ai/privacy) | Inference can remain local; review any optional discovery, download or online features. |
| Custom OpenAI-compatible endpoint | Supplied by endpoint operator | Supplied by endpoint operator | Adam CodexHub cannot determine the endpoint's operator, jurisdiction or data practices. |

## User checklist

Before enabling a remote provider:

1. Identify the legal entity operating the endpoint and the processing region.
2. Review current terms, privacy policy, retention, training controls and subprocessors.
3. Confirm price, quota, retry and refund rules.
4. Use an appropriately scoped, revocable API key.
5. Remove secrets, personal data and confidential material not required for the task.
6. For organizational use, confirm authorization, DPA requirements and international-transfer safeguards.
7. Test with non-sensitive data before using the provider for project work.

An acknowledgement in Adam CodexHub records that the notice was shown. It is not acceptance of a provider's contract on the provider's behalf and is not described as GDPR consent.
