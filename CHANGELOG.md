# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/). All packages share one version.

## [Unreleased]

## [0.3.0-preview] - 2026-11-24

The Kashier provider: payments, lookups, refunds, voids, webhooks and redirects, checked end to end on Kashier's sandbox. Kashier payouts are not included yet.

### Added
- `PayGate.Kashier` settings: `AddKashier(configure, accountName)` registers a Kashier account with `KashierAccountOptions` (merchant id, secret key, Payment API Key, optional transfer key, payment page defaults). They are validated when the app starts: the keys must be set, and a Kashier host must match the environment, so test keys can't be pointed at live hosts or the reverse.
- `PayGate.Kashier` transport:
  - one HTTP client per account serves both the API and FEP hosts
  - HTTP/1.1 is pinned
  - every request body carries a `Content-Length`, because Kashier's FEP host never answers a chunked one
  - credentials are read per call, so a tenant store can supply them
  - responses are read leniently
- `PayGate.Kashier` payments: `AddKashier` registers a gateway for hosted payment sessions.
  - Create returns a redirect to Kashier's payment page. Every session carries the `customer.reference` Kashier requires; it is the merchant reference when the app gives none.
  - Lookups read the session's outcome, by session id or by merchant reference, with the amount, captured and refunded totals, card or wallet, the decline reason in English and Arabic, and `KashierPaymentData` (order id, fees, VAT, settlement).
  - Partial, repeated and remainder refunds go to the right Kashier order.
  - `IPaymentCancelFeature` voids a paid or authorized payment.
  - `KashierPaymentOptions` sets the page language, failure tries, failure redirect and 3-D Secure per payment.
  - A reference that already has a paid order is refused before creating, because Kashier would accept it and then fail the customer silently (`CheckReferenceBeforeCreate`, on by default).
- `PayGate.Kashier` webhooks and redirects: `AddKashier` also registers a webhook verifier, a return parser and an ack policy.
  - Webhooks are checked against `x-kashier-signature` and fail closed: no `signatureKeys`, a listed field missing or `null`, or an unsigned amount, currency, reference, status or transaction id is refused.
  - The event decides the kind and the status decides the outcome, so a successful refund never reads as a payment, and an authorization never reads as a capture. A refund carries its own amount.
  - A redelivered webhook keeps its `EventId`, and one the app already has is answered 409.
  - Kashier doesn't sign the event name, so `ConfirmWithProvider` is on by default for Kashier accounts: each event's status is read back from Kashier before the app sees it. **Handlers act on `Status`, not `Type`.**
  - Return callbacks are verified with Kashier's fixed-field scheme, and a tampered one claims no status.
- `PayGate.Testing`: `KashierWebhookBuilder` builds Kashier payment webhooks (`Paid`, `Declined`, `Authorized`, `Captured`, `Refunded`, `Voided`, or any event through `Build`), shaped like the recorded ones and signed with the account's Payment API Key, so apps can test their handlers without Kashier.
- `PayGate.Abstractions`: `PaymentCapabilities.RejectsDuplicateReference` says whether a reused merchant reference is always refused. It's set for the fake and not for Kashier.
- [Kashier setup and findings](docs/providers/kashier.md):
  - setup: keys, the webhook URL, handling events, the return page, testing without Kashier, and known quirks
  - findings: what Kashier's sandbox actually does, recorded from 2026-11-22 to 2026-11-24, with sanitized fixtures re-signed with a dummy key under `tests/fixtures/kashier`
- Sample app (not published): runs on the Kashier sandbox when `Payments:Account` is `kashier`, with the keys in user secrets.

## [0.2.0-preview] - 2026-11-16

ASP.NET Core integration. `PayGate.Kashier` is still published with no API: it gets one in 0.3.0-preview.

### Added
- `PayGate.AspNetCore` webhook endpoints: `MapPayGateWebhooks()` maps `POST` and `GET {prefix}/{provider}/{account?}`, and `IPayGateWebhookEndpoint` does the same from an MVC controller. The raw body reaches the verifier byte for byte, a body over `MaxBodyBytes` (256 KB by default) is answered 413 before verifying, and the response comes from the provider's ack policy.
- `PayGate.AspNetCore`: one log line per webhook with the account, outcome, status code, event ids and duration; `IPayGateReturnCallbacks` to read return callbacks and check redirect targets against `AllowedReturnUrls`; and a body limit per provider.
- `PayGate`: `AddReturnParser<T>()` for provider packages.
- [Receiving webhooks](docs/webhooks.md): setup for minimal APIs and controllers, public URLs and tunnels, and the dedupe contract apps must follow.
- Sample app (`samples/PayGate.Sample.WebApi`, not published): app-owned orders, attempts and webhook inbox on SQLite, a new attempt per checkout, amount checks, fulfillment once, partial and full refunds, a return page that asks the provider, and a reconciliation job. It runs on the fake gateway, with end-to-end tests.

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

[Unreleased]: https://github.com/off-a7/PayGate/compare/v0.3.0-preview...HEAD
[0.3.0-preview]: https://github.com/off-a7/PayGate/compare/v0.2.0-preview...v0.3.0-preview
[0.2.0-preview]: https://github.com/off-a7/PayGate/compare/v0.1.0-preview...v0.2.0-preview
[0.1.0-preview]: https://github.com/off-a7/PayGate/releases/tag/v0.1.0-preview
