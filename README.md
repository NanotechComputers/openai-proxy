# Azure OpenAI Proxy for JetBrains (and other OpenAI‑compatible clients)

A small, pragmatic proxy that makes Azure OpenAI look like the standard OpenAI API so you can point JetBrains and other
OpenAI‑compatible tools at your own Azure models without needing a JetBrains Enterprise account.

It exposes familiar OpenAI endpoints (like /v1/chat/completions and /v1/models), translates them to Azure OpenAI
requests, and streams responses back to the client. The project is a .NET 9 app, uses Aspire for containerized
workflows, and is instrumented with OpenTelemetry. A Scalar API Reference UI is also included via the Aspire AppHost.

Important: This proxy is intended to run locally or inside a controlled/private environment. Do not expose it publicly
without proper network controls and secrets management.

## Why this exists

JetBrains supports connecting to a custom Azure OpenAI endpoint only with an Enterprise subscription (to handle secret
key configuration). If you don’t have Enterprise access, you can still use your Azure OpenAI credits by putting this
OpenAI‑compatible proxy in the middle. Your IDE or client thinks it’s talking to OpenAI; the proxy translates to Azure
and forwards the responses.

## Features

- OpenAI‑compatible endpoints so existing clients work unmodified:
    - POST /v1/chat/completions
    - POST /v1/responses
    - GET /v1/models (and a few common probe paths)
- Automatic translation of OpenAI‑style requests to Azure OpenAI format
- Streams responses for long/streaming chats
- Aspire integration for multi‑service containerization and health checks
- OpenTelemetry logging/metrics/traces (optional OTLP export)
- Scalar API Reference UI hosted by the Aspire AppHost

## How it works (high level)

- Your client calls an OpenAI‑style endpoint on this proxy
- The proxy builds the correct Azure URL, adds the api-key header, and forwards the request
- The Azure response (including streaming) is proxied straight back to the caller

## Quick start

Prerequisites:

- .NET 9 SDK
- Docker Desktop (optional, for Aspire/docker‑compose flows)
- An Azure OpenAI resource with a deployed chat model (e.g., gpt-5-chat)

1) Clone and configure

- Copy appsettings.json in OpenAI Proxy.ApiService and set the Azure section (see Configuration below). Do not commit
  real keys.

2) Run locally (without containers)

- From the repo root:
    - dotnet run --project "OpenAI Proxy.ApiService"
- The API will listen on the usual ASP.NET dev ports. Check the console log for the bound URLs.

3) Run via Aspire (with Docker)

- From the repo root:
    - dotnet run --project "OpenAI Proxy.AppHost"
- The AppHost can generate docker‑compose artifacts in OpenAI Proxy.AppHost/docker-compose-artifacts. You can run docker
  compose up -d from there if preferred.
- The AppHost also hosts the Scalar API Reference UI, registered against the ApiService.

## Configuration

You can configure via appsettings.json or environment variables.

appsettings.json example (recommended for local dev):

```json
{
  "Azure": {
    "Base": "https://{your-azure-resource}.openai.azure.com",
    "Deployment": "gpt-5-chat",
    "ApiVersion": "2025-01-01-preview",
    "Key": "<your-azure-api-key>"
  }
}
```

Notes:

- Use Base + Deployment + ApiVersion to let the proxy construct URLs automatically; or
- Provide a complete EndpointFull URL and the proxy will use it directly (query string api-version is handled
  automatically if missing).
- ApiVersion defaults to 2025-01-01-preview if not set.

Environment variables (for containers/CI):

- Azure__Base=https://{your-azure-resource}.openai.azure.com
- Azure__Deployment=gpt-5-chat
- Azure__ApiVersion=2025-01-01-preview
- Azure__Key=your-azure-api-key

ASP.NET Core uses double underscores to map environment variables to nested configuration keys (Azure:Key ->
Azure__Key).

Security reminder: Never commit real keys. Use environment variables, secret stores, or Docker secrets for production.

## Endpoints exposed by the proxy

Implemented (source: OpenAI Proxy.ApiService/Program.cs):

- POST /v1/chat/completions → forwarded to Azure /openai/deployments/{Deployment}/chat/completions
- POST /v1/responses → forwarded to Azure /openai/deployments/{Deployment}/responses
- POST /v1/{**rest} → generic catch‑all forwarder under the specified deployment
- GET /v1/models → returns the configured deployment id as a model (e.g., gpt-5-chat)
- GET /v1/chat/completions/models and GET /v1/responses/models → model probe helpers
- GET /models and GET /api/v0/models → minimal compatibility helpers
- OPTIONS on common paths → returns 200 for harmless preflight probes

Health checks (when Development environment):

- GET /health and GET /alive via ServiceDefaults

OpenAPI/Scalar:

- OpenAPI document is exposed by the ApiService (MapOpenApi)
- Scalar API Reference UI is wired in the AppHost and points to the ApiService

## Using with JetBrains IDEs

Many JetBrains features and 3rd‑party tools can work against an “OpenAI‑compatible” endpoint that exposes the standard
OpenAI paths.

Typical approach:

- Start the proxy locally (see Quick start)
- Configure your tool to use:
    - Base URL: http://localhost:{port}/v1
    - API Key: any non‑empty string will usually satisfy the client’s key check, but this proxy ignores it and uses
      Azure:Key instead

If your JetBrains IDE or plugin allows specifying a custom OpenAI base URL, point it to the proxy. If it only supports
the official OpenAI endpoint, consider setting a system‑wide HTTP proxy, a wrapper script, or using a plugin/extension
that supports OpenAI‑compatible endpoints. Exact UI names vary by IDE/version.

## Curl examples

- List models
  curl http://localhost:5080/v1/models

- Create a chat completion (streaming if you set stream=true)
  curl -X POST http://localhost:5080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
  "model":"gpt-5-chat",
  "messages":[{"role":"user","content":"Hello!"}],
  "stream":false
  }'

Replace port 5080 with whatever port the ApiService binds to on your machine or in Docker.

## Deployment notes

- Prefer running locally or inside a private network; do not expose publicly
- Provide Azure:Key via secrets (environment or secret store). Never hard‑code in appsettings.json for shared repos
- Aspire can generate docker‑compose artifacts. You can run them as-is or integrate into your environment
- OpenTelemetry export is enabled when OTEL_EXPORTER_OTLP_ENDPOINT is set; otherwise metrics/traces stay local

## Troubleshooting

- 401/403 errors: Usually an Azure key/permissions issue. Validate Azure:Key and that the Deployment name matches your
  Azure deployment
- 404 from Azure: Check the Deployment name and ApiVersion
- Hanging requests: Ensure outbound access to your Azure region and that streaming is allowed through any
  proxies/firewalls
- CORS/preflight: The proxy returns 200 to OPTIONS on /v1/*, /models, and /api/v0/models to satisfy harmless probes

## Roadmap / ideas

- Additional endpoint coverage beyond chat/responses, if needed
- Optional request/response redaction logs for debugging
- Pluggable auth in front of the proxy if you must host it beyond localhost

## License

Apache License 2.0. No warranty. Use at your own risk.
