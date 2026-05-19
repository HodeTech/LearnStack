# Dapr Sidecar (Dev)

Cross-cutting infrastructure runtime per
[ADR-0014 (Adopt Dapr)](../../docs/decisions/0014-adopt-dapr.md). Three
building blocks are adopted; everything else is **out of scope** per ADR-0014
non-goals.

| Building block | Backend (dev) | Component file | Application interface |
|----------------|---------------|----------------|-----------------------|
| Pub/Sub | Kafka (`kafka:9092`) | `components/pubsub-kafka.yaml` | `IEventBus` (Phase 02b) |
| State store | Redis (`redis:6379`) | `components/statestore-redis.yaml` | `ICacheService` (Phase 02a) |
| Secret store | Vault (`http://vault:8200`, dev mode) | `components/secretstore-vault.yaml` | `ISecretProvider` (Phase 02a) |

Service invocation, workflow, bindings, actors, configuration, and distributed
lock are **not adopted**; if a future need appears the gate is a new ADR.

## Sidecar topology

Dev compose runs one sidecar bound to the `learnstack-api` app id:

```
┌──────────────────────────┐   ┌─────────────────────────────────────┐
│ dotnet run                │   │ daprd                                │
│  → http://localhost:5080  │   │  ./daprd -app-id learnstack-api \   │
│                           │   │           -app-port 5080 \           │
│                           │◄──┤           -dapr-http-port 3500 \    │
│                           │   │           -dapr-grpc-port 50001 \   │
│                           │   │           -placement-host-address \  │
│                           │   │             dapr-placement:50005 \   │
│                           │   │           -components-path /comp \   │
│                           │   │           -config /config/...        │
└──────────────────────────┘   └─────────────────────────────────────┘
```

The .NET host runs **outside the container network** during active dev
(developers `dotnet run` from their workstation). The Dapr sidecar inside the
compose network calls back to `host.docker.internal:5080` in production-shape
deployments; the dev compose default targets `learnstack-api:5080` and Phase 02b
documents how to switch when the .NET host moves inside compose.

The placement service (`dapr-placement`) is required even though actors are
out of scope — `daprd` won't start without it.

## Application access pattern

Per ADR-0014 + Standards 20, modules **never** import `Dapr.Client`. They
consume:

```csharp
public interface IEventBus { Task PublishAsync<T>(T @event, CancellationToken ct) where T : IIntegrationEvent; }
public interface ICacheService { Task<T?> GetAsync<T>(string key, CancellationToken ct); /* … */ }
public interface ISecretProvider { Task<string> GetSecretAsync(string key, CancellationToken ct); /* … */ }
```

`DaprEventBus`, `DaprCacheService`, `DaprSecretProvider` are the **only**
Dapr-aware types in the codebase; they live in `LearnStack.Infrastructure`
and ship in Phase 02b. Architecture tests
`Dapr_SDK_Types_NotImportedOutsideInfrastructure`,
`Modules_DoNotReference_DaprPackage`, and
`ICacheService_Is_OnlyCacheAbstraction` keep this honest.

## Dev credentials

| Surface | Credential |
|---------|------------|
| Vault root token | `learnstack-dev-root-token` |
| Kafka auth | none (`authType: none`, `disableTls: true`) |
| Redis password | (empty) |

All dev-only. Production wires Vault with AppRole / Kubernetes auth and
loads the token through Dapr's `secretKeyRef` indirection so the literal
token never appears in the component YAML.

## What does NOT live here

- The `IEventBus` / `ICacheService` / `ISecretProvider` implementations —
  Phase 02b (`LearnStack.Infrastructure`).
- Outbox dispatcher (`OutboxProcessor` polling + dispatch) — Phase 02b.
- Per-module `inbox_messages` table + `IInboxGuard` — Phase 02b.
- Production Vault setup (HA mode, auto-unseal, AppRole policies) — Phase 11.
