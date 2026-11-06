# PayGate

A set of NuGet packages that give .NET applications **one consistent API** for payment gateways (Kashier first, then Paymob, Fawry and Stripe), covering payment creation, status lookup, refunds and webhooks.

PayGate is a library, not a service:
- **Your application keeps ownership** of its database, payment records, orders, invoices, subscriptions, credentials and business rules.
- There is no central payment database and nothing extra to run.

> **Status: pre-release.** `PayGate.Abstractions` has its contracts and models, which may still change before 1.0. The other packages have no API yet.

## Packages

| Package | Purpose |
|---|---|
| `PayGate.Abstractions` | Contracts and models only (no DI or HTTP dependencies) |
| `PayGate` | Core: DI builder, gateway resolution, HTTP pipeline, crypto and formatting helpers, webhook processing |
| `PayGate.AspNetCore` | Webhook endpoints for minimal APIs and MVC controllers |
| `PayGate.Kashier` | Kashier provider, including Kashier payouts |
| `PayGate.Testing` | Fake gateway and signed webhook builders for application tests |

Paymob, Fawry and Stripe packages follow in 1.x releases.

Requirements: **.NET 10** (`net10.0`).

## Installing from the private feed

Packages are published to GitHub Packages under `off-a7`. Add the feed **with package source mapping** to the consuming application's `NuGet.Config`:

```xml
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
    <add key="paygate" value="https://nuget.pkg.github.com/off-a7/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
    <packageSource key="paygate">
      <package pattern="PayGate.*" />
      <package pattern="PayGate" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

Store the credentials outside the repository. Locally:

```
dotnet nuget update source paygate --username <github-user> --password <PAT with read:packages> --store-password-in-clear-text
```

In CI, use a secret.

> **Why source mapping is required:** packages whose names start with `PayGate` may also exist on nuget.org. The mapping makes NuGet fetch `PayGate*` **only** from the private feed, which prevents dependency-confusion attacks.

## Building

```
dotnet restore
dotnet build -c Release
dotnet test -c Release --filter-not-trait "Category=Sandbox" --ignore-exit-code 8
dotnet pack eng/PayGate.Packages.slnf -c Release -o artifacts/packages
./eng/consumer-check.ps1
```

The SDK version is pinned in [`global.json`](global.json).

## Documentation

- [Contributing](CONTRIBUTING.md)
- [Changelog](CHANGELOG.md)
