---
name: add-feature-gated-ui
description: >
  Wire entitlement-aware UI in `apps/web` using `useFeatureFlag(FeatureKey)` and
  `useLimit(LimitKey)` hooks that read the LearnStack entitlement projection.
  USE FOR: hiding / showing a tab, button, or screen based on plan-level features;
  showing `current/limit` usage with soft / hard enforcement signalling; upgrade
  nudges that link to Hub-side plan management. DO NOT USE FOR: client-side
  authorisation (the API enforces; UI mirrors), greying-out features instead of
  hiding (always hide), or in-app upgrade forms (storefront billing is Hub-side).
---

# Adding feature-gated UI

## Purpose

Render entitlement-aware UI correctly: plan-projected `FeatureKey` toggles hide
features; `LimitKey` usage visualisations surface `current/limit` plus soft/hard
semantics; upgrade nudges link out to the Hub plan-management page (or fall back
to a `mailto:` on Self-Hosted). All of this lives in
[14-frontend-architecture.md § Entitlement-Aware UI](../../../docs/architecture/14-frontend-architecture.md)
+ [21-feature-flags.md](../../../docs/architecture/21-feature-flags.md).

## When to use

- A tab / nav item / screen / button is conditional on a plan-projected feature.
- A workflow has a numeric limit the user should see (concurrent live sessions,
  classroom minutes per month, storage GB).
- A blocked action should surface an upgrade hint.

## When not to use

- Authorisation (permission checks). Those use `auth().permissions.includes(...)`,
  not feature flags.
- Greying-out a feature instead of hiding. Three UI patterns the project picked:
  **hide** for missing features, **visualise** for limits, **block-then-link** for
  exceeded hard limits.
- Embedding an upgrade-now form inline. The tenant's *own* LearnStack plan is
  managed on the Hub side, not in `apps/web`.

## Inputs

| Input | Required | Description |
|-------|----------|-------------|
| Key | Yes | `FeatureKey` from `@learnstack/sdk/feature-keys` or `LimitKey` from `@learnstack/sdk/limit-keys`. |
| UI surface | Yes | Tab, button, screen, banner. |
| Behaviour on missing | Yes | Hide (default) for FeatureKey; `current/limit` visualisation for LimitKey. |

## Workflow

### Step 1: Feature gating with `useFeatureFlag`

```tsx
import { useFeatureFlag } from "@learnstack/sdk/hooks";
import { FeatureKeys } from "@learnstack/sdk/feature-keys";

export function StudioNav() {
  const customDomainEnabled = useFeatureFlag(FeatureKeys.CustomDomain);
  const ssoSamlEnabled = useFeatureFlag(FeatureKeys.SsoSaml);

  return (
    <nav>
      <NavItem href="/dashboard">Dashboard</NavItem>
      <NavItem href="/users">Users</NavItem>
      {customDomainEnabled && <NavItem href="/settings/domains">Custom domain</NavItem>}
      {ssoSamlEnabled && <NavItem href="/settings/sso">SSO</NavItem>}
    </nav>
  );
}
```

Rule: **hide, don't disable**. A disabled tab teases the user with a feature they
don't have; a missing tab is honest.

### Step 2: Limit visualisation with `useLimit`

```tsx
import { useLimit } from "@learnstack/sdk/hooks";
import { LimitKeys } from "@learnstack/sdk/limit-keys";

export function UsageMeter() {
  const { current, limit, soft } = useLimit(LimitKeys.MaxLearners);
  if (limit === 0) return null;   // 0 means "no limit imposed"

  const pct = (current / limit) * 100;
  const tone =
    pct >= 95 ? "danger" :
    pct >= 80 ? "warning" :
    "neutral";

  return (
    <div className={`usage-meter usage-meter--${tone}`}>
      <span>{current} / {limit}</span>
      <progress value={current} max={limit} />
      {soft && pct >= 100 && (
        <p>You've exceeded the included quota — usage continues but extra charges may apply.</p>
      )}
    </div>
  );
}
```

Tone:

- < 80% — neutral.
- 80–95% — warning.
- > 95% — danger.
- 100% + soft — banner, action allowed.
- 100% + hard — blocked at the API (the action returns
  `403 ProblemDetails type=urn:learnstack:errors:limit-exceeded`). The frontend
  renders the Problem Details message inline.

### Step 3: Blocked action handling

When the API returns a hard-limit error, render the message and surface an
upgrade link:

```tsx
async function onCreateLearner() {
  const result = await sdk.identity.createLearner(payload);
  if (result.errorType === "urn:learnstack:errors:limit-exceeded") {
    showUpgradeBanner({
      message: result.detail,
      cta: deploymentMode === "SelfHosted"
        ? { label: "Contact your administrator", href: "mailto:support@learnstack.dev" }
        : { label: "Manage subscription", href: hubManagementUrl() },
    });
    return;
  }
  // success path
}
```

Rule: **never** render an in-app form to change the plan. The tenant's plan lives
on the Hub side; LearnStack only displays read-only state.

### Step 4: Killswitch behaviour

Killswitches show as feature flags but their semantics differ — they can be off
even for a tenant whose plan enables the feature. Always render the unhappy path
honestly:

```tsx
const recordingEnabled = useFeatureFlag(FeatureKeys.ClassroomRecording);
// useFeatureFlag returns the *effective* answer: killswitch overlay applied.
if (!recordingEnabled) {
  return <p>Recording is currently disabled platform-wide. Please try again later.</p>;
}
```

The killswitch overlay logic lives in the hook, not in the page — don't replicate.

### Step 5: Cache + invalidation

The hooks read from the `platform_entitlement_cache` projection via the SDK. The
client-side cache:

- 60s TTL.
- Eagerly invalidated when the SDK receives a server-sent
  `learnstack.hub.entitlement` event (the BFF translates the Dapr event into an
  SSE notification).

Plan upgrades reflect within seconds, not 60s; the TTL is the safety net.

### Step 6: Tests

```tsx
test("Custom domain tab is hidden when feature is off", async () => {
  const { queryByText } = renderWithEntitlement(<StudioNav />, {
    features: { "tenancy.custom_domain": false },
  });
  expect(queryByText("Custom domain")).toBeNull();
});

test("Learner limit shows danger tone at 96%", async () => {
  const { container } = renderWithEntitlement(<UsageMeter />, {
    limits: { "tenancy.max_learners": 100 },
    state: { current: 96 },
  });
  expect(container.querySelector(".usage-meter--danger")).not.toBeNull();
});
```

## Validation

- The Studio nav matches the tenant's plan: features absent on the plan are
  absent in the UI.
- Limit visualisations correctly transition through neutral / warning / danger
  tones at the documented thresholds.
- Hard-limit errors render the Problem Details message + upgrade CTA.
- The CTA URL is **Hub-side** for SaaS / Dedicated, and a `mailto:` (or
  customer-defined channel) for Self-Hosted.
- A killswitch flip overrides the projection within seconds (test via the SSE
  notification path).

## Common pitfalls

- **Greying-out instead of hiding.** Wrong UX pattern.
- **In-app upgrade form.** The tenant's plan belongs on the Hub side; LearnStack
  shows read-only state. Even on Self-Hosted there's no in-app upgrade UI.
- **Calling `useFeatureFlag` outside a Client Component.** The hook depends on
  React context; Server Components fetch via the SDK directly
  (`sdk.entitlement.isEnabled(...)`).
- **Hardcoding the Hub URL.** The deployment mode determines it; use
  `useDeployment()`.
- **Per-locale branching on entitlement copy.** The hook returns booleans /
  numbers; locale rendering is the i18n bundle's job.
- **Skipping the killswitch case.** A killswitch can disable a feature the user's
  plan otherwise includes; show a clear "temporarily disabled" message, not the
  same UI as missing feature.
- **Re-rendering on every entitlement change.** The hooks memoise; don't wrap in
  extra state.
