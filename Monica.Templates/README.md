# Monica Templates

Official `dotnet new` templates for the shortest path from an empty directory to a running Monica application.

## Install from a package

```bash
dotnet new install Monica.Templates@1.0.0-rc.15
dotnet new monica-api --name Acme.Orders
cd Acme.Orders
dotnet run
```

Open the URL printed by ASP.NET Core, then visit:

- `/` for the starter discovery document
- `/health` for readiness and `/alive` for process liveness
- `/metrics` for the Prometheus scrape endpoint

Use `--framework-version <VERSION>` when the application must target a Monica version other than the template default.

## Maintain the embedded version

The source template version should match `Directory.Build.props` during normal development:

```bash
python3 eng/sync-template-version.py --check
```

Synchronize it explicitly when preparing a versioned package:

```bash
python3 eng/sync-template-version.py --version 1.0.0
```

The release workflow runs the synchronization command with the resolved tag or dispatch version before building and packing.

## Test a local package

```bash
dotnet pack Monica.Templates/Monica.Templates.csproj --configuration Release
dotnet new install Monica.Templates/bin/Release/Monica.Templates.*.nupkg
```

## Package maturity

The starter intentionally references only Stable-tier packages:

- `Monica.Core` for host-bound composition and module lifecycle
- `Monica.HealthCheck` for native readiness, liveness, and sanitized host health snapshots
- `Monica.OpenTelemetry` for production-compatible metrics

Provider integrations are opt-in after the application has a concrete infrastructure choice. Labs packages are excluded from the default template so experimental capabilities never become accidental production dependencies.
