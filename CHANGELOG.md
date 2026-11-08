# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/). All packages share one version.

## [Unreleased]

## [0.1.0-preview] - 2026-11-08

First preview. `PayGate.AspNetCore` and `PayGate.Kashier` are published with the same version but have no API yet: they get one in 0.2.0-preview and 0.3.0-preview.

### Added
- Repository foundation: solution, strict build settings, central package management, public API tracking, versioning from git tags, CI, release and nightly sandbox workflows.
- Package shells: `PayGate.Abstractions`, `PayGate`, `PayGate.AspNetCore`, `PayGate.Kashier`, `PayGate.Testing`.
- `PayGate.Abstractions`: provider and account identity, `Money`/`Currency`, payment creation, lookup, refund and cancel models, normalized status with transition rules, `PaymentResult<T>`/`PaymentError`, capabilities, gateway contracts, and webhook/event/return-callback models.
- `PayGate` provider helpers: HMAC-SHA256/512 and SHA-256 with fixed-time hex/base64 signature comparison, and culture-proof wire formatting (fixed decimals, minor units, RFC 3986 encoding, raw JSON values for signing).
- `PayGate` registration: `AddPayGate()` with `AddEventHandler<T>()` and `AddGateway(...)` for provider packages, gateways resolvable by account and as keyed services, named account options validated at startup with redacted `ToString()`, and a replaceable credentials provider.
- `PayGate` provider HTTP clients: one per account, writes never retried unless they carry an `Idempotency-Key`, reads retried with back-off, per-attempt and total timeouts, transport failures on writes reported as `OutcomeUnknown`, and logs limited to method, host, path, status and duration.
- `PayGate` webhook processing: `IWebhookProcessor` resolves the account, verifies, parses, optionally confirms each payment with the provider per account, and dispatches every event to the app's handler in its own scope. A per-provider `WebhookAckPolicy` maps the outcome to an HTTP status and body. Failures and unconfirmed events answer 5xx so that the provider retries.
- `PayGate` telemetry: an `ActivitySource` and a `Meter` named `PayGate` cover gateway calls and webhook verify and dispatch, for every gateway registered with `AddGateway`. Tags carry the provider, account, operation and outcome only.
- `PayGate.Testing`: `FakePaymentGateway` for the redirect, client-SDK and pay-later flows with controllable outcomes, `FakeWebhookBuilder` for signed webhooks, and `AddPayGateFake()`, which refuses to start in Production unless allowed.

[Unreleased]: https://github.com/off-a7/PayGate/compare/v0.1.0-preview...HEAD
[0.1.0-preview]: https://github.com/off-a7/PayGate/releases/tag/v0.1.0-preview
