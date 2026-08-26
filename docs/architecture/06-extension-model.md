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

The core platform talks to external systems through **interfaces** in
`LearnStack.SharedKernel`:

| Concern | Interface |
|---------|-----------|
| Payment processing | `IPaymentProvider` |
| Email delivery | `IEmailProvider` |
| SMS delivery | `ISmsProvider` |
| WhatsApp delivery | `IWhatsAppProvider` |
| Object storage | `IFileStorageService` |
| Search | `ISearchProvider` (or `ITenantSearch` / `IPlatformSearch` per ADR-0012) |
| Live classroom transport | `ILiveClassProvider` |
| Recording egress | `IRecordingEgressProvider` |
| Identity provider | (covered by Keycloak baseline; ADR-0004) |
| Pub/Sub | `IEventBus` → in-process now; Dapr → Kafka target (ADR-0038) |
| Cache | `ICacheService` → in-memory now; Dapr → Valkey target (ADR-0038) |
| Secret store | `ISecretProvider` → configuration now; Dapr → Vault target (ADR-0038) |

Implementations live in `LearnStack.Infrastructure.<Concern>.<Provider>` projects.
Modules never import provider SDK types. Architecture tests enforce this — same pattern
as the original Extension Model document. **This part is unchanged.**

### 1b. Tenant-driven customization — the new extension surface

What ADR-0011 originally called "vertical extension points" — content types, page
blocks, lesson item types, scoring rules, level taxonomies, completion rules, custom
fields — are now **per-tenant database rows**, not compile-time-typed registrations from
a third-party DLL.

| Customization surface | Where it lives | Examples |
|-----------------------|----------------|----------|
| Content types | `tenant_content_types` | VocabularyCard, AsanaPose, CodeChallenge, Score |
| Page blocks | `tenant_page_blocks` | VocabularyGallery, AsanaSequenceBrowser, LeaderboardWidget |
| Lesson item types | `tenant_lesson_item_types` | SpeakingPractice, GuidedSequence, CodeRunner, DrivingSimulation |
| Level taxonomies | `tenant_level_taxonomies` | CEFR (A1-C2), yoga difficulty (Beginner-Master), kyu/dan |
| Scoring rules | `tenant_scoring_rules` | CEFR placement DSL, code challenge auto-grading rules |
| Completion rules | `tenant_completion_rules` | "all items viewed AND quiz score >= passing_threshold" |
| Custom fields | `tenant_custom_field_defs` | Extra fields on `User`, `Course`, `Enrollment`, etc. |
| Notification templates | `tenant_template_library` | Liquid / Handlebars per channel + locale |

Every row carries `tenant_id`, RLS-isolated. Some are org-scoped as well.

The frontend renders these through a **closed, generic primitive set** (text, markdown,
image, video, audio, pdf, code, math, link, list, tabs, sanitised-html) composed by a
small fixed set of composite renderers (default-card, content-list, lesson-shell,
quiz-shell, …). Adding a new primitive or composite is a LearnStack release; tenants
**cannot** bring custom JSX.

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
- **Tenant feature enablement.** Now via `IFeatureFlags` reading the entitlement
  projection ([ADR-0021](../decisions/0021-feature-based-entitlement.md) Amendment 1),
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
  - cefr                [A1, A2, B1, B2, C1, C2]
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
  - yoga-difficulty     [Beginner, Intermediate, Advanced, Master]
tenant_scoring_rules: (none — yoga isn't graded)
```

### CodeAcademy (coding bootcamp)

```
tenant_content_types:
  - code-challenge      { title, language, starter_code, test_suite, hints }
tenant_lesson_item_types:
  - code-runner-item    { challenge_ref, time_limit_minutes, max_attempts }
tenant_level_taxonomies:
  - coding-difficulty   [Easy, Medium, Hard, Expert]
tenant_scoring_rules:
  - auto-grade-runner   ← runs test suite against submission
```

Same modules. Same code. Different rows.

## 5. Provider adapter ports (unchanged from earlier doc)

Existing `LearnStack.SharedKernel` ports for external integrations remain. Concrete
implementations:

| Interface | Implementation |
|-----------|----------------|
| `IPaymentProvider` | `StripePaymentProvider`, `IyzicoPaymentProvider`, `OfflinePaymentProvider` |
| `IEmailProvider` | `PostmarkEmailProvider`, `ResendEmailProvider`, `SmtpEmailProvider` |
| `ISmsProvider` | `TwilioSmsProvider`, `NetGsmSmsProvider` |
| `ILiveClassProvider` | `LiveKitSelfHostedProvider`, `LiveKitCloudProvider` |
| `IFileStorageService` | `MinioFileStorageService`, `S3FileStorageService` |
| `IEventBus` | `InProcessEventBus` (lands with the port in Packet 5), `DaprEventBus` (demand-gated to Phase 11) |
| `ICacheService` | `InMemoryCacheService` (lands with the port in Packet 5), `DaprCacheService` (demand-gated to Phase 11) |
| `ISecretProvider` | `ConfigurationSecretProvider` (**registered today**, shipped in Packet 3), `DaprSecretProvider` (demand-gated to Phase 11) |
| `IEntitlementProvider` | `NullEntitlementProvider` (Packet 9), `HubEntitlementProvider` (Phase 02c), `SignedLicenseKeyEntitlementProvider` (skeleton from Hub `P02c-6`, hardened in Phase 11) |

The last three rows are the demand-gated set from
[ADR-0035](../decisions/0035-demand-gated-infrastructure.md): the port and its default
ship together, and the vendor adapter ships in the phase named against its written
trigger. Only `ISecretProvider` has shipped so far — the other two ports and their
defaults land in [Phase 02a Packet 5](../roadmap/phase-02a-kernel-tenancy.md). The
in-process implementations are not a development convenience: once registered they are
the only implementations in **every** deployment mode until Phase 11.

Adding a new provider is a code change in core (new adapter implementation in
`LearnStack.Infrastructure`) — not a tenant action.

## 6. Frontend rendering of customization data

A tenant's content types are rendered through a fixed pipeline:

```
Tenant defines content type "VocabularyCard" with JSON Schema
                      ↓
Module reads tenant_content_types where tenant_id = current
                      ↓
For each JSON Schema field, frontend matches to a PRIMITIVE_RENDERER
  (text, markdown, image, video, audio, pdf, code, math, link, list, tabs)
                      ↓
Composite renderer (default-card / content-list / lesson-shell) composes the primitives
                      ↓
Tenant's brand tokens applied as CSS variables
                      ↓
Rendered output
```

Architecture tests enforce:

- The primitive renderer set is closed (only what's in `PRIMITIVE_RENDERERS`).
- The composite renderer set is closed (only what's in `COMPOSITE_RENDERERS`).
- Adding to either set requires CODEOWNERS approval — LearnStack team only.

## 7. What this means for `IModule`

`IModule` (LearnStack's module-loading contract) remains, but slimmer than the
Nexora-pattern `IModuleExtension`. **No `IModuleExtension` interface in LearnStack.** The
"vertical module = third-party DLL implementing `IModuleExtension`" Nexora pattern does
not apply.

LearnStack's own modules (`LearnStack.Modules.Identity`, `LearnStack.Modules.Tenancy`,
`LearnStack.Modules.Content`, `LearnStack.Modules.Catalog`,
`LearnStack.Modules.Enrollment`, `LearnStack.Modules.Classroom`,
`LearnStack.Modules.Audit`, etc.) implement `IModule`. All are first-party. Tenants do
**not** install third-party modules.

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
  composite (slow path, CODEOWNERS gate). The closed set is intentional — it prevents
  the platform from becoming a wild west.
- **JSON Schema authoring is harder than writing a C# class.** Admin Studio needs a
  visual schema editor (Phase 06+) to make this approachable for non-technical tenant
  admins.

## 9. Phasing

| Phase | Deliverable |
|-------|-------------|
| 02 | Customization tables created (empty). Primitive renderer set scaffolded. `IModule` interface in SharedKernel. Provider adapter interfaces in SharedKernel + Null implementations. |
| 04 | CMS / Page Builder: page blocks as data; first composite renderers; JSON form editor for content types. |
| 05 | Catalog / Learning Content: lesson item types as data; lesson player composites. |
| 06 | Admin Studio visual schema editor (drag-and-drop field builder); preview pane. |
| 08a | Assessment: scoring rule DSL + sandbox; level taxonomies; completion rules. |
| 12 (optional) | Content template marketplace: pre-built JSON Schema + scoring rule + level taxonomy bundles for tenants to install with one click. Data sharing only, no code. |

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
