# Contributing to PayGate

## Prerequisites
- .NET SDK exactly as pinned in [`global.json`](global.json). The SDK decides the version of the implicit
  `Microsoft.NET.ILLink.Tasks` package that the lock files record, so CI and every machine must use the same one.
  To upgrade: change `global.json`, run `dotnet restore --force-evaluate`, and commit both.
- PowerShell (Windows PowerShell 5.1 or PowerShell 7) for the scripts in `eng/`.

## Branching
- `main` is always releasable. Work happens on short-lived branches: `feature/<issue>-<slug>`, `fix/<issue>-<slug>`.
- Every change goes through a pull request. CI must be green, and one review is required.

## Commits
Format: `#<issue> type(scope): summary`, followed by bullet points for the body.
- Types: `feat`, `fix`, `refactor`, `test`, `docs`, `build`, `ci`, `chore`.
- Scopes: the package or area, e.g. `core`, `abstractions`, `aspnetcore`, `kashier`, `testing`, `build`, `ci`, `docs`.
- Prefer several focused commits (roughly one per layer) over one large commit.

## Build, test, pack
```
dotnet restore
dotnet build -c Release
dotnet format --verify-no-changes
dotnet test -c Release --filter-not-trait "Category=Sandbox" --ignore-exit-code 8
dotnet pack eng/PayGate.Packages.slnf -c Release -o artifacts/packages
./eng/consumer-check.ps1
```

Notes:
- Tests run on **Microsoft.Testing.Platform** (opted in via `global.json`), so `dotnet test` uses its syntax: `--filter-trait`/`--filter-not-trait`, `--project`, `--coverage`. Exit code 8 means "zero tests ran". The sandbox project produces it on PR runs, where its tests are filtered out.
- `eng/PayGate.Packages.slnf` lists only the shipped `src/` projects. Pack through it.

Rules:
- **Warnings are errors.** Don't suppress a rule without a comment explaining why.
- **Library code uses `ConfigureAwait(false)`** (CA2007 is enforced under `src/`).
- **Public API changes** must be recorded in the project's `PublicAPI.Unshipped.txt` (the analyzer offers a code fix).
- **Lock files** (`packages.lock.json`) are committed. After changing a package, run `dotnet restore` and commit the updated lock files.
- **New packages** go into `Directory.Packages.props`. Use the latest stable version that is at least two weeks old.

### Sandbox tests
Tests that call real provider sandboxes are tagged `[Trait("Category", "Sandbox")]`. They skip themselves when their credentials (environment variables such as `KASHIER_MERCHANT_ID`) are absent. They run nightly in CI, never on pull requests.

## Versioning and releases
- Versions come from git tags via MinVer (`v1.2.3`). All packages share one version.
- To release: update `CHANGELOG.md`, merge, then tag `vX.Y.Z` on `main`. The release workflow publishes to GitHub Packages.
- Breaking changes are only allowed in major versions after 1.0.

## Adding a provider package
1. If the provider needs anything new in `PayGate.Abstractions`, agree the design first: new members must not break existing providers.
2. Create `src/PayGate.<Provider>` (referencing `PayGate`) with `README.md` and `PublicAPI.*.txt`.
3. Create `tests/PayGate.<Provider>.Tests`. Inherit the conformance suite from `tests/PayGate.Conformance`, and add golden signature vectors and recorded sandbox fixtures under `tests/fixtures/<provider>/`.
4. Add a signed webhook builder to `PayGate.Testing`.
5. Document setup in `docs/providers/<provider>.md`.
6. Add the package to `eng/consumer-check` and to the package table in `README.md`.
