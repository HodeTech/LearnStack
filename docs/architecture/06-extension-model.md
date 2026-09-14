# Extension Model

> **Note:** This document was rewritten on 2026-05-18 as a consequence of
> [ADR-0018: Tenant-Driven Customization Model](../decisions/0018-tenant-driven-customization-model.md),
> which supersedes ADR-0011's typed vertical-pack extension registry. The previous version
> of this document, which described `IModuleExtension` and the `IExtensionRegistry`, is
> obsolete; vertical packs are not part of the LearnStack design.

LearnStack core is 100% domain-agnostic. Tenants extend it by declaring **data**, not by
shipping code. Yoga, coding, music, language, exam prep, art, certification, driving
school — every customer runs on the same compiled binary; differentiation lives in their
database rows.

This document is the architecture-level overview. The full data model and worked examples
live in [32-tenant-customization-model.md](32-tenant-customization-model.md).

## 1. Two kinds of "extension" we still talk about

The word "extension" appears in this codebase in two senses; ADR-0018 narrows it:

### 1a. Provider adapters — still active

The core platform's provider boundary uses **interfaces**, with the intended contracts
below. The foundation ports are implemented; the other contracts arrive with their
consumers. [§ 5](#5-provider-adapter-ports) identifies the defaults that run today.

| Concern | Interface |
|---------|-----------|
| Payment processing | `IPaymentProvider` |
| Email delivery | `IEmailProvider` |
| SMS delivery | `ISmsProvider` |
| WhatsApp delivery | `IWhatsAppProvider` |
| Object storage | `IFileStorageService` |
| Search | `ITenantSearch` / `IPlatformSearch` per ADR-0012 |
| Live classroom transport | `ILiveClassProvider` |
| Recording egress | `IRecordingEgressProvider` |
| Identity provider | (covered by Keycloak baseline; ADR-0004) |
| Pub/Sub | `IEventBus` → in-process now; Dapr → Kafka target (ADR-0038) |
| Cache | `ICacheService` → in-memory now; Dapr → Valkey target (ADR-0038) |
| Secret store | `ISecretProvider` → configuration now; Dapr → Vault target (ADR-0038) |

Implementations live behind the adapter boundary, per
[Architecture Standards § Provider Adapters](../standards/01-architecture-standards.md#provider-adapters).
Modules never import provider SDK types; architecture tests enforce that boundary.

### 1b. Tenant-driven customization — the new extension surface

What ADR-0011 originally called "vertical extension points" — content types, page
blocks, lesson item types, scoring rules, level taxonomies, completion rules, custom
fields — are now **per-tenant database rows**, not compile-time-typed registrations from
a third-party DLL.

| Customization surface | Where it lives | Examples |
|-----------------------|----------------|----------|
| Content types | `tenant_content_types` | `vocabulary-card`, `asana-pose`, `code-challenge`, `score` |
| Page blocks | `tenant_page_blocks` | `vocabulary-gallery`, `asana-sequence-browser`, `leaderboard-widget` |
| Lesson item types | `tenant_lesson_item_types` | `speaking-practice`, `guided-sequence`, `code-runner`, `driving-simulation` |
| Level taxonomies | `tenant_level_taxonomies` | CEFR (A1-C2), yoga difficulty (Beginner-Master), kyu/dan |
| Scoring rules | `tenant_scoring_rules` | CEFR placement DSL, code challenge auto-grading rules |
| Completion rules | `tenant_completion_rules` | "all items viewed AND quiz score >= passing_threshold" |
| Custom fields | `tenant_custom_field_defs` | Extra fields on `MembershipProfile`, `Course`, `Enrollment`, etc.; tenant fields belong to the membership, not the global user |
| Notification templates | `tenant_template_library` | Liquid / Handlebars per channel + locale |

Every row carries `tenant_id`, RLS-isolated. Some are org-scoped as well.

The frontend renders these through the **closed, generic primitive and composite sets**
in [Tenant Customization Model § 2](32-tenant-customization-model.md#2-generic-primitive-renderers),
whose sanitised-HTML primitive is `embed-html`. Adding a primitive or composite is a
LearnStack release; tenants **cannot** bring custom JSX.

## 2. What the new model preserves

The original Extension Model document's invariants are preserved by ADR-0018:

- **Core stays generic.** No `Cefr`, `Asana`, `CodeChallenge`, `EnglishPlacement`,
  `YogaSequence` etc. in any LearnStack module. Architecture test
  `Core_Modules_HaveNo_DomainSpecific_Names` enforces this
  ([canonical spelling](../standards/21-architecture-tests-catalogue.md)); the folder
  rule is the separate `No_Source_Folder_Named_Verticals`.
- **Anti-patterns to reject** (still apply):
  - Adding domain-specific columns to core entities.
  - Importing live-classroom SDK types in Domain or Application.
  - Hardcoding tenant ids in code (configuration is per-tenant data).
  - Feature flags scattered without a registry (typed `FeatureKeys`, ADR-0021).
- **Tenant feature enablement.** Via `IFeatureFlags` composing over
  `IEntitlementProvider` ([ADR-0045](../decisions/0045-entitlement-and-feature-flag-socket.md)),
  not via "vertical loaded but not enabled."

## 3. What the new model removes

Old artifacts that **do not exist** in LearnStack:

- ❌ `LearnStack.Verticals.*` source folder. The "vertical assembly" concept is gone.
- ❌ `IModuleExtension` interface. There are no plugin DLLs.
- ❌ `IExtensionRegistry` typed registry. Customization happens through CRUD on the
  customization tables, not via startup-time registrations from third-party code.
- ❌ `tenant_extensions` table. Tenants don't "enable" verticals; they author
  customization data directly.

## 4. Example: how three tenants share the same binary

### English Hero (language learning)

```
tenant_content_types:
  - vocabulary-card     { word, definition, pronunciation, audio_url, level }
  - grammar-point       { rule, examples, exercises }
tenant_lesson_item_types:
  - speaking-practice   { prompt, scoring_rubric, expected_duration_sec }
tenant_level_taxonomies:
  - cefr                [a1, a2, b1, b2, c1, c2]
tenant_scoring_rules:
  - cefr-placement-v1   ← sandboxed DSL expression
```

### Anatolia Yoga (yoga studio)

```
tenant_content_types:
  - asana-pose          { english_name, sanskrit_name, image_urls, difficulty, benefits }
  - breath-technique    { name, instructions, duration_minutes }
tenant_lesson_item_types:
  - guided-sequence     { poses[], music_url, intro_audio_url }
tenant_level_taxonomies:
  - yoga-difficulty     [beginner, intermediate, advanced, master]
tenant_scoring_rules: (none — yoga isn't graded)
```

### CodeAcademy (coding bootcamp)

```
tenant_content_types:
  - code-challenge      { title, language, starter_code, test_suite, hints }
tenant_lesson_item_types:
  - code-runner-item    { challenge_ref, time_limit_minutes, max_attempts }
tenant_level_taxonomies:
  - coding-difficulty   [easy, medium, hard, expert]
tenant_scoring_rules:
  - submission-score    ← evaluates already-recorded submission results
```

The lists contain item keys; localized band names live in `display_name`, per
[ADR-0018's key rule](../decisions/0018-tenant-driven-customization-model.md#2026-09-04--customization-keys-and-item-keys-are-lowercase).
The coding example declares content and evaluates facts. Executing a submission is
subject to [Platform Vision § Genericity boundary](01-platform-vision.md#genericity-boundary).

## 5. Provider adapter ports

The provider-specific adapters below are intended choices, not shipped implementations.
The [roadmap](../roadmap/README.md) owns their delivery phases. The foundation rows
identify the defaults that run today.

| Interface | Implementation |
|-----------|----------------|
| `IPaymentProvider` | `StripePaymentProvider`, `IyzicoPaymentProvider`, `OfflinePaymentProvider` |
| `IEmailProvider` | `PostmarkEmailProvider`, `ResendEmailProvider`, `SmtpEmailProvider` |
| `ISmsProvider` | `TwilioSmsProvider`, `NetGsmSmsProvider` |
| `ILiveClassProvider` | `LiveKitSelfHostedProvider`, `LiveKitCloudProvider` |
| `IFileStorageService` | SeaweedFS through its S3-compatible API ([Phase 04](../roadmap/phase-04-cms-media-pages.md)) |
| `IEventBus` | `InProcessEventBus` (registered today, Packet 5), `DaprEventBus` (demand-gated to Phase 11) |
| `ICacheService` | `InMemoryCacheService` (registered today, Packet 5), `DaprCacheService` (demand-gated to Phase 11) |
| `ISecretProvider` | `ConfigurationSecretProvider` (**registered today**, shipped in Packet 3), `DaprSecretProvider` (demand-gated to Phase 11) |
| `IEntitlementProvider` | `NullEntitlementProvider` (registered today, Packet 9), `HubEntitlementProvider` (Phase 02c), `SignedLicenseKeyEntitlementProvider` (skeleton from Hub `P02c-6`, hardened in Phase 11) |

The last four rows are the demand-gated set from
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md): the port and its default
ship together, and the vendor adapter ships in the phase named against its written
trigger. All four ports and their defaults have shipped — `ISecretProvider` in Packet 3,
`IEventBus` and `ICacheService` in Packet 5, `IEntitlementProvider` in Packet 9 of
[Phase 02a](../roadmap/phase-02a-kernel-tenancy.md) — and only the vendor adapters wait on
their triggers. The defaults run in **every** deployment mode until each port's own
trigger selects its adapter.

Adding a new provider is a code change in core (new adapter implementation in
`LearnStack.Infrastructure`) — not a tenant action.

## 6. Frontend rendering of customization data

A tenant's content types follow this intended rendering pipeline; its first consumer
lands in [Phase 02d](../roadmap/phase-02d-walking-skeleton.md):

```
Tenant defines content type "vocabulary-card" with JSON Schema
                      ↓
Consumer resolves definitions through Customization.Application.Contracts
                      ↓
For each JSON Schema field, frontend matches to a PRIMITIVE_RENDERER
  (the canonical set in Tenant Customization Model § 2)
                      ↓
Composite renderer (default-card / content-list / lesson-shell) composes the primitives
                      ↓
Tenant's brand tokens applied as CSS variables
                      ↓
Rendered output
```

The [architecture-test catalogue](../standards/21-architecture-tests-catalogue.md)
records the registry checks and their implementation status. Renderer vocabulary is a
platform change; tenant data selects from the registered keys. No CODEOWNERS file
exists today ([Contributing](../../.github/CONTRIBUTING.md)), so it is not a mechanical
approval gate.

## 7. First-party module registration

No `IModule` interface exists in the shipped runtime. Module services are registered at
the composition root; [Module Boundaries](03-module-boundaries.md) owns the layout.
All LearnStack modules are first-party. Tenants do **not** install third-party modules
or implement `IModuleExtension`.

## 8. Risks and trade-offs

- **Loss of compile-time type safety.** Vertical pack registrations were typed; data-
  driven customization is JSON-schema-typed. Mitigation: schema validation on every save;
  primitive renderer pipeline is type-safe in TypeScript.
- **Some advanced features can't be expressed declaratively.** Audio analysis, generative
  AI feedback, video proctoring, etc. These become LearnStack-team-built features
  available via plan entitlement (ADR-0021). Tenants don't bring their own code;
  LearnStack ships the feature, gates it by plan.
- **Renderer composite set is closed.** A tenant wanting a truly novel UI pattern must
  either compose existing primitives (fast path) or request LearnStack to add a new
  composite through the [platform registry and review process above](#6-frontend-rendering-of-customization-data).
  Tenant definitions continue to select registered keys; expanding that vocabulary
  is a platform change.
- **JSON Schema authoring is harder than writing a C# class.** Admin Studio needs a
  visual schema editor (Phase 06+) to make this approachable for non-technical tenant
  admins.

## 9. Phasing

The delivery sequence and shipped state live in
[Tenant Customization Model § 12](32-tenant-customization-model.md#12-phasing).

## References

- ADR-0018 — Tenant-Driven Customization Model (this document is the architecture-level
  view; ADR-0018 is the decision).
- ADR-0011 — Vertical Extension Points (superseded; retained in `docs/decisions/` with
  Superseded status banner).
- ADR-0013 — Page Block Schema Versioning.
- ADR-0038 — Cross-Cutting Port and Event Contracts (including the retained Dapr
  provider-adapter choice).
- ADR-0021 — Feature-Based Entitlement (plan-gated features that go beyond customization).
- [32-tenant-customization-model.md](32-tenant-customization-model.md) — deep dive with
  schema, worked examples, sandbox engine, Admin Studio surface.
- [01-platform-vision.md](01-platform-vision.md) — why generic-only core is the product.
