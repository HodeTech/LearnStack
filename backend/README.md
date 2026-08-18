# LearnStack Backend

.NET 10 modular monolith — see [docs/standards/01-architecture-standards.md](../docs/standards/01-architecture-standards.md)
for the module-layout rules and [docs/architecture/03-module-boundaries.md](../docs/architecture/03-module-boundaries.md)
for the dependency direction this scaffold enforces.

## Layout

```
backend/
  LearnStack.slnx                          # solution (modern XML format)
  Directory.Build.props                    # net10.0, nullable, warnings-as-errors
  Directory.Packages.props                 # central package management
  global.json                              # SDK pin

  src/
    LearnStack.SharedKernel/               # ids, results, kernel primitives
    LearnStack.Domain/                     # core domain (cross-module shared)
    LearnStack.Application.Contracts/      # cross-module contracts root
    LearnStack.Application/                # composition pipeline (MediatR + validators)
    LearnStack.Infrastructure/             # EF + cross-cutting infra
    LearnStack.Infrastructure.Audit/       # ADR-0033 audit pipeline plumbing
    LearnStack.Api/                        # ASP.NET host

    Modules/
      Tenancy/                             # 4-package layout per module
      Identity/
      Customization/                       # ADR-0018 tenant customization aggregates
      Audit/                               # AuditEntry aggregate (ADR-0033)
      Content/
      Media/
      Education/
        LearnStack.Modules.Education.Application/
        LearnStack.Modules.Education.Application.Contracts/
        LearnStack.Modules.Education.Domain/
        LearnStack.Modules.Education.Infrastructure/

  tests/
    LearnStack.Tests.Unit/
    LearnStack.Tests.Integration/          # Testcontainers (Postgres/Valkey)
    LearnStack.Tests.Architecture/         # NetArchTest + filesystem invariants
    LearnStack.Tests.Contract/
```

**No `Verticals/` folder** — ADR-0018 supersedes ADR-0011. Tenant-specific shapes live as
**tenant customization data** owned by the `Customization` module, not as code. The
`No_Source_Folder_Named_Verticals` architecture test enforces this.

## Prerequisites

- **.NET SDK 10.0.x is required** (pinned in [global.json](global.json)). The
  `rollForward: latestFeature` policy only walks within the 10.0 feature band —
  a workstation with only .NET 9 installed will fail at SDK resolution. Install
  the .NET 10 SDK from <https://dotnet.microsoft.com/download/dotnet/10.0>
  before `dotnet build`.
- A local infrastructure stack — see [../infra/compose/dev.yml](../infra/compose/dev.yml).

## Common Commands

```bash
# Restore + build
dotnet build LearnStack.slnx

# Run the API (defaults to http://localhost:5080)
dotnet run --project src/LearnStack.Api

# Tests by suite
dotnet test tests/LearnStack.Tests.Unit
dotnet test tests/LearnStack.Tests.Architecture
dotnet test tests/LearnStack.Tests.Integration       # needs docker for Testcontainers
dotnet test tests/LearnStack.Tests.Contract
```

The `make` orchestrator and CI workflow land in a later Phase 01 package.
