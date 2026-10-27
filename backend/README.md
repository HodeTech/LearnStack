# LearnStack Backend

.NET 10 modular monolith — see [docs/standards/01-architecture-standards.md](../docs/standards/01-architecture-standards.md)
for the module-layout rules and [docs/architecture/03-module-boundaries.md](../docs/architecture/03-module-boundaries.md)
for the dependency direction the source enforces.

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
    LearnStack.Tools.IdentityAuthorityInstaller/ # deployment-only identity anchor
    LearnStack.Tools.Seeder/               # finite development data/adoption

    Modules/
      Tenancy/                             # 4-package layout per module
      Identity/                            # global binding + scoped foundation
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
    LearnStack.Tests.Integration/          # offline HTTP + real Postgres/Keycloak
    LearnStack.Tests.Architecture/         # NetArchTest + filesystem invariants
    LearnStack.Tests.Contract/
```

**No `Verticals/` folder** — ADR-0018 supersedes ADR-0011. Tenant-specific shapes live as
**tenant customization data** owned by the `Customization` module, not as code. The
`No_Source_Folder_Named_Verticals` architecture test enforces this.

## Prerequisites

- **.NET SDK 10.0.112 or later in the 10.0 line is required** (pinned in
  [global.json](global.json), the same version CI builds with). 10.0.112 is the floor
  because its runtime, 10.0.12, carries the .NET security fixes 10.0.0 did not. The
  `rollForward: latestFeature` policy only walks forward within 10.0 — a workstation with
  only .NET 9, or an older 10.0 SDK, will fail at SDK resolution. Install
  the .NET 10 SDK from <https://dotnet.microsoft.com/download/dotnet/10.0>
  before `dotnet build`.
- A local infrastructure stack — see [../infra/compose/dev.yml](../infra/compose/dev.yml).
- Install the matching identity authority before API startup, following
  [Identity deployment](../docs/modules/identity/README.md#global-implementation-and-deployment).
  Runtime uses application credentials; the migration credential stays deploy-only.

## Common Commands

```bash
# Restore + build
dotnet build LearnStack.slnx

# Run the API with its runtime configuration already supplied
# (defaults to http://localhost:5080)
dotnet run --project src/LearnStack.Api

# Tests by suite
dotnet test tests/LearnStack.Tests.Unit
dotnet test tests/LearnStack.Tests.Architecture
dotnet test tests/LearnStack.Tests.Integration       # needs docker for Testcontainers
dotnet test tests/LearnStack.Tests.Contract
```

The direct API command reads process/.NET configuration, not the root `.env`.
For the owned local API and native HTTPS web lifecycle, run `make demo` from
the repository root; it projects the selected private environment. The
[root quick start](../README.md#quickstart) owns setup, and
[test guidance](../.claude/skills/run-tests-locally/SKILL.md) owns suite selection.
