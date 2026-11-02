# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/). All packages share one version.

## [Unreleased]

### Added
- Repository foundation: solution, strict build settings, central package management, public API tracking, versioning from git tags, CI, release and nightly sandbox workflows.
- Empty package shells: `PayGate.Abstractions`, `PayGate`, `PayGate.AspNetCore`, `PayGate.Kashier`, `PayGate.Testing`.
- `PayGate.Abstractions`: provider and account identity, `Money`/`Currency`, payment creation, lookup, refund and cancel models, normalized status with transition rules, `PaymentResult<T>`/`PaymentError`, capabilities, gateway contracts, and webhook/event/return-callback models.
- `PayGate` provider helpers: HMAC-SHA256/512 and SHA-256 with fixed-time hex/base64 signature comparison, and culture-proof wire formatting (fixed decimals, minor units, RFC 3986 encoding, raw JSON values for signing).
- `PayGate` registration: `AddPayGate()` with `AddEventHandler<T>()` and `AddGateway(...)` for provider packages, gateways resolvable by account and as keyed services, named account options validated at startup with redacted `ToString()`, and a replaceable credentials provider.
- `PayGate` provider HTTP clients: one per account, writes never retried unless they carry an `Idempotency-Key`, reads retried with back-off, per-attempt and total timeouts, transport failures on writes reported as `OutcomeUnknown`, and logs limited to method, host, path, status and duration.
