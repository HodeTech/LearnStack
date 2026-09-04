# ADR-0043: What LearnStack Does With a Tenant-Authored Payload

## Status

Accepted

**Date:** 2026-09-04 **Deciders:** @platform

## Decision Drivers

- **The customization model rests on a validator that nothing in the corpus
  names.** [ADR-0018](0018-tenant-driven-customization-model.md) makes JSON
  Schema "the lingua franca" and
  [Tenant Customization Model § 8.1](../architecture/32-tenant-customization-model.md)
  requires that a `json_schema` be checked as "itself a valid JSON Schema (draft
  2020-12)" on save. No document says which implementation performs that check,
  and `backend/Directory.Packages.props` carries no JSON Schema package. The
  first consumer lands in
  [Phase 02a Packet 8](../roadmap/phase-02a-kernel-tenancy.md); the choice cannot
  be postponed past it.
- **The read path trusts the database, so the write path is the only gate.**
  § 8.1 states it plainly: "**Nothing is schema-validated on the read path**", and
  the consequence is that "anything that can write a content-entry row without
  going through the validating command path … breaks that trust for every
  subsequent read." A validator that is merely *present* is not the requirement;
  a validator that is the *only* door is.
- **A library's defaults are not a contract, and this library's defaults are
  wrong for this use in four measured ways.** Its default dialect is not
  2020-12; a tenant's own `$schema` line silently selects a different one and
  survives every attempt to pin it; a tenant's `$id` is written into
  process-global state; and its default output format carries no location for
  the Problem Details § 8.1 promises. Every one of those is invisible until it
  is measured, and each one turns a rule the corpus already states into a rule
  the code does not keep. § Context records the measurements.
- **The payload is authored by a lower-trust actor.** A tenant's content editor
  writes the schema. A schema document is a recursive structure with references
  and regular expressions, and both are attacker-controlled. § 8.4 bounds
  nesting depth; the corpus bounds neither references nor regexes, and both are
  reachable inside every limit it does declare.
- **The rule-body column type must be settled before the engine is chosen.**
  Five places in the corpus state that rules may be authored before the engine
  exists — [Phase 05](../roadmap/phase-05-education-learning-content.md)
  ("Rules can be authored before the engine exists; they cannot be **evaluated**
  before it exists", and again where it makes the migration of "rows authored
  under one `dialect` before the decision" a deliverable of ADR-0025 itself) and
  [Phase 02a](../roadmap/phase-02a-kernel-tenancy.md) twice. The shape those five
  build on lives only in roadmap prose. Roadmap prose is a plan, not a decision
  record.
- **The corpus mandates a cache on a cost claim that is not true.**
  [§ 8.2](../architecture/32-tenant-customization-model.md) says "Compiling a
  JSON Schema is the expensive part" and that the compiled form "never needs
  invalidating" because "a published schema version is immutable" — while
  [§ 4](../architecture/32-tenant-customization-model.md) of the same document
  grants an additive in-place edit under an unchanged `schema_version`. The
  premise and the justification are both wrong, and they fail in opposite
  directions: compiling is measured **cheaper** than evaluating at every
  realistic schema size, and the immutability that would make the cache safe
  does not hold. A decision is owed on both.

## Considered Options

### The validator implementation

| Option | Draft 2020-12 | JSON stack | Licence | Verdict |
|---|---|---|---|---|
| **`JsonSchema.Net` 8.0.5** (json-everything) | Native; `MetaSchemas.Draft202012` and `Dialect.Draft202012` are first-class | `System.Text.Json` | MIT (expression), whole resolved graph | **Chosen** |
| `JsonSchema.Net` 9.x | Same | Same | NuGet **binary** under the Open Source Maintenance Fee EULA | Rejected for now — see § Context |
| `NJsonSchema` 11.6.1 | Partial, and the gap is silent | `Newtonsoft.Json` 13.0.3 in every target group, and in the public API (`Validate(JToken, …)`) | MIT | Rejected — measured, five of six 2020-12 keywords are parsed, round-tripped, and then **ignored at validation**, so an instance that must be rejected is accepted |
| `Newtonsoft.Json.Schema` 4.0.1 | Yes | Newtonsoft | **AGPL-3.0**, plus a runtime quota | Rejected — copyleft follows the binary into every Self-Hosted deployment ([ADR-0020](0020-triple-deployment-hybrid-license.md)), and the quota fires **in the customer's own process** |
| Hand-rolled subset evaluator | Whatever we build | STJ | — | Rejected — the corpus promises tenants "JSON Schema 2020-12", and a subset that silently accepts what it does not understand is the `NJsonSchema` failure mode by construction |

### Where the schema is checked

| Option | Verdict |
|---|---|
| **Four ordered gates: JSON, profile, meta-schema, build** | **Chosen.** They are four different checks and no one of them subsumes another — measured, see § Context |
| Parse and build only, trusting `FromText` to mean "valid schema" | Rejected — it accepts four dialects LearnStack does not ship, admits `true` / `false` / `{}` as schemas, and reports no location |
| Meta-schema evaluation only | Rejected — it accepts every one of those too, plus documents that cannot be built at all |

### The rule-body column

| Option | Verdict |
|---|---|
| **`body text NOT NULL` + `dialect text NOT NULL` with a `CHECK`** | **Chosen** |
| `jsonb` | Rejected — [ADR-0018](0018-tenant-driven-customization-model.md) fixes the rule document's own form as YAML, and YAML is not JSON |
| A per-engine column added when ADR-0025 lands | Rejected — five corpus statements already promise tenants may author rules before the engine exists |

## Decision

### 1. The dialect is draft 2020-12; the implementation is `JsonSchema.Net` 8.0.5, behind a port

Every `json_schema` a tenant authors is a draft 2020-12 document. The evaluator
is `JsonSchema.Net` (`Json.Schema` namespace, `System.Text.Json`-native),
**pinned at 8.0.5** in `Directory.Packages.props`, with the reason carried in a
comment beside the version — the pin is a licence ceiling, not a routine hold,
and a bump to 9.x changes the terms of the artifact rather than only its
behaviour.

It is reached through a port, `IJsonSchemaValidator`, declared in
`LearnStack.SharedKernel`. That placement is not the provider-adapter rule —
[Architecture Standards § Provider Adapters](../standards/01-architecture-standards.md)
governs "every external dependency that crosses the LearnStack boundary", and an
in-process evaluator crosses nothing. It is the foundation-port rule, and the
reason is consumer count: Customization needs it in Packet 8, Identity needs it
for `tenant_custom_field_defs` in [Phase 03](../roadmap/phase-03-identity-admin.md),
Content needs it for the validating bulk importer in
[Phase 04](../roadmap/phase-04-cms-media-pages.md), and Education needs it in
[Phase 05](../roadmap/phase-05-education-learning-content.md). A port in
`Customization.Application.Contracts` would make three unrelated modules depend
on Customization to reach a library wrapper.

**The adapter builds with per-call options and never with the library's
defaults:**

```csharp
JsonSchema.FromText(text, new BuildOptions
{
    Dialect        = Dialect.Draft202012,   // default is https://json-schema.org/v1/2026
    SchemaRegistry = new SchemaRegistry(),  // default is process-global
});
```

Neither argument is optional and neither is cosmetic; § Context measures what
each one prevents. `BuildOptions.Default` is a cached singleton and must not be
mutated to achieve this — that is process-global state shared with anything else
in the host.

### 2. A schema is admitted only after four ordered gates

All four run on the write path. A failure in any returns
`Result.Fail(validation_failed, …)` naming a JSON pointer where the gate can
produce one, and the ordering is chosen so that the gates which *can* name a
location run before the one that cannot.

1. **It is JSON.** `JsonDocument.Parse`. Failure is
   `System.Text.Json.JsonException` — including reader depth, whose runtime type
   `JsonReaderException` is **internal** and cannot be named in a `catch`.
2. **It is inside the LearnStack schema profile** (§ 3, § 4, § 6, and § 8.4's
   declared limits). This gate is LearnStack's own; it is what the library does
   not do.
3. **It satisfies the 2020-12 meta-schema.**
   `MetaSchemas.Draft202012.Evaluate(…, new EvaluationOptions { OutputFormat = OutputFormat.List })`.
   `OutputFormat.List` is not a preference: the default is `Flag`, which returns
   pass/fail and no location at all.
4. **It builds.** `JsonSchema.FromText` with the options in § 1. Failure is
   `JsonSchemaException`, which carries a message and no location — which is why
   it is last, and why gate 3 is where a tenant's structural mistake is named.

Gates 3 and 4 are both kept because neither subsumes the other, in both
directions: the meta-schema accepts documents the builder rejects, and the
builder accepts documents the profile must reject. § Context measures both
directions.

### 3. The schema profile — identity, references, and shape

A tenant-authored `json_schema` is admitted only if all of the following hold.
Each exists because the library's behaviour without it was measured, not because
the draft requires it.

| Rule | Why |
|---|---|
| The root carries `"$schema": "https://json-schema.org/draft/2020-12/schema"`, exactly | A tenant's `$schema` line selects the dialect and **overrides the pinned one** — measured. Without this rule, `prefixItems` is silently inert under a tenant-supplied draft-07 line and the same schema text accepts and rejects the same instance |
| The root is an object schema declaring `properties` | `true`, `false` and `{}` are all valid 2020-12 schemas and all pass gates 3 and 4. `true` and `{}` turn validation off for every entry of that content type; `false` makes the type unusable. § 8.1's read-time structural pass "walks the entry's JSON against the schema's field list", and there is no field list without `properties` |
| No `$id`, at the root or nested | `$id` is not a reference — it declares a schema-resource identity, and the evaluator records it. Under the default registry a tenant's `$id` becomes process-wide state; the per-build registry in § 1 removes the mechanism, and forbidding the keyword removes the need to reason about it |
| `$ref` is fragment-only (`#…`) | An absolute `$ref` is a resolution attempt against something outside the document. `$defs` and `#/$defs/…` remain available |
| No `$dynamicRef` / `$dynamicAnchor` | Their whole purpose is late binding across documents; there is no second document |
| Nesting and reference limits per § 8.4 | Measured on the syntax tree. A reference **cycle** needs no separate limit: the builder detects it (§ Context) |

### 4. Unknown keywords pass — because the pinned dialect says so

`x-renderer` / `x-taxonomy` / `x-language` are unknown keywords, and draft 2020-12
admits them. That is a property of **the dialect the adapter pins**, not of the
library: under the library's default dialect an unknown keyword is a hard
`JsonSchemaException` (measured). Resolving those extensions against the
renderer, taxonomy and language registries is § 8.1's separate requirement,
performed by LearnStack after the four gates — never delegated to the validator,
which has no opinion about them.

### 5. `pattern`, `patternProperties` and `propertyNames` are not admitted

A tenant-authored regular expression is executed by the platform against
tenant-authored input. On the pinned version the library builds every such
`Regex` with **`MatchTimeout = Regex.InfiniteMatchTimeout`** and exposes no knob
to change it (measured), and evaluation cost is exponential in input length:
`^(a+)+$` against 32 characters took **30.2 seconds** on one core, in one
property, on one save.

The three mitigations that suggest themselves were measured and do not work:

- **`maxLength` beside `pattern`.** JSON Schema keywords are independent
  assertions; `maxLength: 16` against a 33-character input did not short-circuit
  the pattern — the evaluation still had not returned at 30 seconds.
- **A process-wide regex timeout.** `RegexMatchTimeoutException` never arises,
  because the library's `Regex` already carries an explicit infinite timeout,
  which overrides the process default.
- **Cancelling the evaluation.** Cancellation frees the request thread; the core
  stays pinned.

So the keyword is refused, and the refusal is cheap: the entire corpus uses
`pattern` exactly once, for a BCP-47 language tag, which `LocaleTag` already
owns. `enum`, `const`, `format`, `minLength` and `maxLength` cover what the
worked examples actually declare.

**Trigger for reconsidering** ([ADR-0035](0035-demand-gated-infrastructure.md)
shape): a validator release that lets the caller supply `RegexOptions` or a
`MatchTimeout`, or a non-backtracking evaluation path. At that point `pattern`
is admitted behind whichever of those exists, with a per-schema regex count and
pattern-length limit in § 8.4. Until then the profile refuses it, and
`RegexMatchTimeoutException` — if a future version ever raises one — maps to
`validation_failed`, never to a 500, for the reason § 3's reference rule already
gives: the author's mistake belongs in the author's response.

### 6. There is no compiled-validator cache

§ 8.2 specifies one, keyed on `(tenant_id, content_type_key, schema_version)`,
held for the process lifetime in a bounded LRU, on the stated ground that
"compiling a JSON Schema is the expensive part". **It is not.** Measured on the
pinned version, compiling costs *less* than evaluating at every size that
matters, and the gap widens as the schema grows — 1.4× at the corpus's own
`vocabulary-card` example, **0.5×** at the 100-property ceiling § 8.4 declares.
The cache would save tens to a few hundred microseconds on a write that is
already paying a database round trip.

Not shipping it is the stronger fix for a second reason. The cache is only sound
if a published `json_schema` never changes, and the corpus does not say that: an
additive edit — a widened enum, an added optional field — raises
`schema_revision` **within** an unchanged `schema_version`
([ADR-0013](0013-page-block-schema-versioning.md),
[§ 4](../architecture/32-tenant-customization-model.md)). A validator keyed on
the version alone therefore outlives the schema it was compiled from and rejects
a payload the current schema accepts. Keying it on the generation counter would
patch that; deleting the cache removes it, and removes with it the question of
which lifecycle stage a `schema_revision` bump belongs to —
[Phase 04 § Customization Key Shape](../roadmap/phase-04-cms-media-pages.md)'s
open question, which this ADR then does not have to prejudge.

The adapter compiles per call. Definition **sets** — the rows themselves, read on
nearly every request and written a handful of times per tenant per month — keep
the § 8.2 cache and the generation counter § 7 places; that ratio is the one that
justifies a cache, and it saves a query rather than a microsecond.

The one keyword whose compilation *is* expensive is `pattern`, which § 5 refuses
for an unrelated reason. Should it ever be admitted, this decision is worth
re-measuring rather than assuming.

### 7. The generation counter is not an aggregate

The counter § 8.2 specifies is a single row per tenant in a Customization-owned
table — `tenant_id uuid PRIMARY KEY, generation bigint NOT NULL DEFAULT 1` —
tenant-owned, tenant-wide, under the canonical template in
[Database Standards § Table classes](../standards/05-database.md). It is bumped
in the same transaction as any customization write, by
`INSERT … ON CONFLICT (tenant_id) DO UPDATE SET generation = … + 1 RETURNING generation`,
because the row does not exist for a tenant that has never had one and because a
read-modify-write loses one of two concurrent bumps.

It is **not** an aggregate root, and the distinction is load-bearing:

- Its port does not derive from `IAggregateWriteStore<TRoot, TId>`, so
  `Cross_Aggregate_Writes_Are_Confined_To_Tenant_Provisioning` still counts one
  aggregate in the handler.
- Its members take `TenantId` — a SharedKernel identifier — and no
  `Customization.Domain` type, so `Every_Write_Port_Is_Countable_Or_Enumerated`
  does not require it to be enumerated.

It is the same class of durable, non-aggregate row as `outbox_messages` and
`idempotency_keys`, which
[ADR-0040 § What the transaction spans](0040-ambient-unit-of-work.md) already
places inside the business transaction. § Aggregate Ownership's cross-aggregate
prohibition and ADR-0042's one-entry allow-list are both untouched. The counter
lives in Customization rather than as a column on `tenants` because a module
writing another module's table is forbidden with no exception at all.

### 8. Rule bodies are opaque text with a dialect discriminator

`TenantScoringRule` and `TenantCompletionRule` — which land in
[Phase 05](../roadmap/phase-05-education-learning-content.md), not here — store:

```sql
body     text NOT NULL,
dialect  text NOT NULL,
CONSTRAINT ck_<table>_dialect CHECK (dialect IN ('cel', 'lua-restricted', 'learnstack-ast'))
```

`body` holds the rule document as authored. Per
[ADR-0018](0018-tenant-driven-customization-model.md) that document is YAML with
a `rules:` list whose leaves carry `condition` expressions, so the column is
`text` because YAML is not JSON — and `dialect` names the language of those
`condition` expressions, which is exactly what ADR-0025 chooses between. The
`CHECK` enumerates its three candidates so a row cannot claim a dialect nobody
has decided on; ADR-0025 narrows the list to one.

The shape is fixed here because five corpus statements promise tenants may author
rules before the engine exists, and one of them makes migrating "rows authored
under one `dialect` before the decision" a deliverable of ADR-0025 itself. Those
promises are unbuildable without a settled column type, and ADR-0025's reserved
scope — "engine … sandbox boundary … allowed function set" — does not include
storage. LearnStack does not parse, validate or evaluate a rule body, before or
after ADR-0025, outside the ADR-0025 sandbox.

## Context

Every measurement below was taken against **`JsonSchema.Net` 8.0.5** (assembly
`8.0.0.0`, informational `8.0.5+3520d7ac`) on .NET 10.0.10, unless a row says
otherwise.

**Parsing, meta-schema evaluation and the profile are three different checks.**

| Document | Gate 3 — meta-schema | Gate 4 — `FromText` | Admitted? |
|---|---|---|---|
| `{"type": 42}` | invalid | `JsonSchemaException` | no |
| `{"required": "word"}` | invalid | `JsonSchemaException` | no |
| `{"minLength": -3}` | invalid | `JsonSchemaException` | no |
| `{"$schema": "…draft-07…", "prefixItems": […]}` | **valid** | **builds** | **no — gate 2** |
| `{"$schema": "https://example.com/mine", …}` | **valid** | `JsonSchemaException` | no |
| `true` / `false` / `{}` | **valid** | **builds** | **no — gate 2** |
| A valid 2020-12 object schema | valid | builds | yes |

Rows four to six are the reason the profile gate exists. The meta-schema types
`$schema` as `format: "uri"` and `EvaluationOptions.Default.RequireFormatValidation`
is `false`, so it accepts any string there — including `not-a-uri`. It is
authoritative about structure and silent about dialect.

**The tenant's `$schema` line wins, and pinning does not take it back.** The
library's `Dialect.Default` is `https://json-schema.org/v1/2026`, whose
`AllowUnknownKeywords` is `false`. Measured:

| Built as | `{"$schema": draft-07, "prefixItems": [{"type":"integer"}]}` vs `[false]` |
|---|---|
| default options | valid — `prefixItems` ignored |
| `BuildOptions { Dialect = Draft202012 }` | **valid — the pin is lost** |
| the same document declaring 2020-12 | invalid — `prefixItems` applied |

So the same schema text accepts or rejects the same instance according to one
tenant-supplied line, and the only defence is to require that line. The same
default dialect is why § 4 has to say *why* unknown keywords pass: a schema
carrying `x-taxonomy` and no `$schema` raises
`JsonSchemaException: Unknown keywords (x-taxonomy) are disallowed for this dialect.`

**A tenant's `$id` is process-global state, and the failure is a cross-tenant
denial of service.** Measured under default options: building a schema with
`$id: https://tenant-a.example/s` registers it; a second build of that `$id`
raises `JsonSchemaException: Overwriting registered schemas is not permitted.`
— permanently, for the life of the process. One tenant registering an `$id`
another tenant uses stops the second tenant from ever saving that schema. With
`new SchemaRegistry()` per build, the same `$id` builds twice with no collision,
and a later document's `$ref` to it does **not** resolve (`RefResolutionException`)
— measured, so the isolation is real in both directions.

**Regexes are unbounded on the pinned version.** Walking the built schema graph
finds the tenant's pattern compiled as
`Regex("^(a+)+$", Options = Compiled | ECMAScript, MatchTimeout = INFINITE)`.
Against `"a" × n + "!"`:

| n | 20 | 25 | 27 | 28 | 30 | 32 |
|---|---|---|---|---|---|---|
| wall | 10 ms | 259 ms | 940 ms | 2.1 s | 8.0 s | **30.2 s** |

Cost is also linear in the number of regex-bearing properties — K = 1 / 4 / 8
measured at 475 / 1885 / 3806 ms of CPU — and § 8.4 permits 100 properties per
content type. `JsonSchema.Net` 9.x carries a five-second `MatchTimeout`, which
bounds one regex and not one request; on either version the keyword needs a
rule, so this is not a consequence of the version pin.

**The ADR's own error-detail requirement removes the cheapest defence.**
`OutputFormat.Flag` short-circuits on first failure; `OutputFormat.List` does
not. Measured at K = 4: 472 ms versus 1885 ms. § 8.1 requires Problem Details
naming a JSON pointer, and only `List` carries locations — so the format the
corpus mandates is the one that evaluates every branch.

**Instance evaluation already produces what RFC 7807 needs.** A failing instance
yields per-location details — `/word :: minLength=Value should be at least 1
characters`, `/extra :: All values fail against the false schema`.

**Compiling is cheaper than evaluating, and the gap widens with size.** Median of
2000 iterations after warm-up, `FromText` versus `Evaluate` with
`OutputFormat.List`:

| Schema | compile | evaluate | ratio |
|---|---|---|---|
| The corpus's own `vocabulary-card` (§ 3) | 19.8 µs | 13.7 µs | 1.4× |
| `$defs` + depth 5 + 40 referencing properties | 94.7 µs | 178.9 µs | **0.5×** |
| 100 properties — § 8.4's declared ceiling | 251.9 µs | 460.3 µs | **0.5×** |

§ 8.2's "compiling a JSON Schema is the expensive part" is false at every one of
these sizes, and most false at the largest. A built schema is also safe to share
— 32 000 evaluations across 32 threads on one instance produced no exception and
no wrong result — so the cache § 6 declines is *possible*; it is simply not worth
its correctness cost. (`JsonSchema` is not fully immutable: `BaseUri` has a
public setter. Nothing on the read path writes it, which is why sharing measures
clean, but "immutable" would be the wrong word for it.)

**Reference cycles need no limit of their own.** A self-recursive `$ref` raises
`JsonSchemaException: Cycle detected starting with a reference to '#/$defs/a'`
at build time. § 8.4's depth limit is measured on the syntax tree; the cycle is
the builder's.

**The evaluator does not fetch remote references, and that is not enough.** A
`$ref` naming `http://10.255.255.1/s.json` — a blackholed address that would
stall any real request — raised `RefResolutionException` in **0 ms**. That makes
§ 3's fragment-only rule a *correctness* decision rather than a
security-of-last-resort one: without it the author's mistake becomes a runtime
failure on a reader's request, and the day a future registry gains a fetch hook,
the write-time gate is the thing that was already there.

**Nesting is bounded twice, and the outer bound is not ours.**
`System.Text.Json` refuses beyond 64 levels: a schema nested 40 levels deep
(two JSON levels per schema level) raised `JsonReaderException` — runtime type
`System.Text.Json.JsonReaderException`, which is **internal**, deriving from the
public `JsonException`. § 8.4's declared limit of 5 is far inside it, and is
checked explicitly so the author gets a 400 naming the limit.

**Why 8.0.5 and not the current release.** `JsonSchema.Net` is MIT through
8.0.5 and, from 9.0.0, ships its NuGet **binary** under the Open Source
Maintenance Fee EULA (`requireLicenseAcceptance=true`,
`<license type="file">OSMFEULA.txt</license>`), as do `JsonPointer.Net` 7.x and
`Json.More.Net` 3.x; the 9.4.0 archive contains no OSI licence text at all. The
EULA leaves the **source** under MIT (§ 3) and places no additional restriction
on redistributing the binary (§ 4), so it is a vendor-side subscription rather
than the per-deployment obligation that disqualifies `Newtonsoft.Json.Schema` —
those two are different in kind, and this ADR does not flatten them. 8.0.5 was
chosen because its whole resolved graph — `JsonPointer.Net` 6.0.1,
`Json.More.Net` 2.2.0, `Humanizer.Core` 3.0.1 — is plain MIT, and because it
reproduces every measurement in this section identically, with the same
`Evaluate(JsonElement, EvaluationOptions)` signature. It is also the **end** of
the MIT line: there is no later MIT-expression release to upgrade into.

**Why `NJsonSchema` is rejected on evidence rather than on purpose.** Its own
package description is "JSON Schema reader, generator and validator for .NET",
and it does validate instances — so "its purpose is not evaluation" would be
false. The disqualifying fact is measured: five of six draft 2020-12 keywords
are parsed and round-tripped into the serialised schema and then **ignored at
validation**, so an instance the schema must reject is accepted. A validator
that silently under-enforces is worse in this position than one that refuses,
because § 8.1 makes the write path the only gate.

## Consequences

### Positive

- The corpus's most-repeated promise — "the schema is a write-time contract, and
  the read path trusts the database" — acquires a named implementation, a stated
  set of gates, and a profile that closes the four holes the library leaves open.
- One port, so replacing the library is one adapter rather than a search.
- Phase 05 can build `TenantScoringRule` against a settled column while ADR-0025
  is still open, which is what five corpus statements already assume.
- A tenant that authors a broken schema learns on save, in Problem Details, at a
  JSON pointer — never on a learner's page load.

### Negative

- Tenants cannot use `pattern`, and the reason is a library limitation rather
  than a product one. The trigger for lifting it is named in § 5.
- 8.0.5 is the last MIT-expression release, so a future upstream fix arrives only
  on a version whose binary carries the maintenance-fee agreement. The pin is a
  ceiling, and the comment beside it in `Directory.Packages.props` says so.
- A third-party dependency becomes load-bearing on every customization write.
  Mitigated by the port and the pin, not by pretending the risk is absent.
- Four gates cost four passes over the document on save. Save is rare
  (§ 8.2: "written a handful of times per tenant per month"); the read path pays
  nothing.
- The `dialect` `CHECK` enumerates three values, two of which will never be used.
  Removing the losers is a one-line migration ADR-0025 owns — and it stays
  one-line because Phase 05 creates the table after the engine is picked.

### Neutral

- `x-` extension keywords stay outside the validator's remit permanently. That is
  the draft's own design, not a limitation of the choice.

## Architecture Tests

| Rule | Asserts |
|---|---|
| `JsonSchema_Net_Types_NotImportedOutsideInfrastructure` | No module assembly and no core assembly depends on the `Json.Schema` namespace; only the adapter project does. `Adapters_Wrap_Provider_Exceptions` does **not** cover this — its forbidden list is a closed enumeration of network-reached provider SDKs, and an in-process evaluator is not one |

Registered in
[the catalogue](../standards/21-architecture-tests-catalogue.md) in the same
commit as this ADR, at Status **Registered**, Phase 02a Packet 8; the test lands
with the adapter it guards.

## Implementation Notes

- Port: `IJsonSchemaValidator` in `LearnStack.SharedKernel.Validation`. Two
  members — admit a schema document, and check an instance against a schema
  document already admitted. Both return `Result`; neither throws; neither
  mentions a `Json.Schema` type in its signature, and no built `JsonSchema`
  outlives a call (§ 6).
- Adapter: `JsonSchemaNetValidator` in `LearnStack.Infrastructure.Validation`.
  It is the only project referencing the package.
- `Directory.Packages.props` gains `JsonSchema.Net` at `8.0.5`, with the licence
  ceiling in a comment beside it.
- Rule-body storage ships as **documentation and a decision** in Packet 8; the
  columns arrive with the tables in
  [Phase 05](../roadmap/phase-05-education-learning-content.md).

## References

- [ADR-0018](0018-tenant-driven-customization-model.md) — the customization model
  this validates the payloads of, and the source of the rule document's YAML form.
- [ADR-0013](0013-page-block-schema-versioning.md) — versioned `(key, schema_version)`
  revisions, and the additive in-place edit that § 6 declines to cache across.
- ADR-0025 (open) — the rule-evaluation engine, whose column type this ADR fixes
  ahead of it.
- [ADR-0040](0040-ambient-unit-of-work.md) — what the business transaction spans,
  and why § 7's counter is inside it.
- [ADR-0042](0042-tenant-provisioning-cross-aggregate-transaction.md) — the
  cross-aggregate allow-list § 7 leaves at one entry.
- [Tenant Customization Model § 8](../architecture/32-tenant-customization-model.md)
  — the runtime cost model, validation timing, cache strategy and declared limits.
- [Phase 04 § Customization Key Shape and Immutable Schema Versions](../roadmap/phase-04-cms-media-pages.md)
  — the owner of the open question in § 6.
