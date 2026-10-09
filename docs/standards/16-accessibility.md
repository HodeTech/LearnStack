# 16 — Accessibility Standards

**Status:** Adopted
**Derives from:** WCAG 2.2 AA (external authoritative standard), [00-principles.md](00-principles.md) § 6 (Foundation First).
Public-page obligations also follow [ADR-0027](../decisions/0027-frontend-i18n.md)
and the [Accepted P02d-6 package](../roadmap/phase-02d-walking-skeleton.md#p02d-6-decision-package-2026-10-09).

LearnStack targets **WCAG 2.2 AA** across all user-facing surfaces.

## Why

LearnStack is an education platform; learners with disabilities are a first-class audience. Accessibility is also a regulatory baseline (KVKK, EAA in EU jurisdictions). It is **never** a future improvement.

## Targets

| Surface | Target |
|---------|--------|
| Public site | WCAG 2.2 AA |
| Learner portal | WCAG 2.2 AA |
| Instructor portal | WCAG 2.2 AA |
| Admin Studio | WCAG 2.2 AA (some advanced editor surfaces may be AAA-deferred with documented justification) |
| Live classroom UI | WCAG 2.2 AA, with extra attention to captions and keyboard-only joining |

## Rules

### Semantic HTML

- Use the right element: `<button>` for actions, `<a>` for navigation, `<nav>`, `<main>`, `<header>`, `<footer>`, `<article>`, `<section>`.
- Headings follow the document outline; no skipped levels.
- One `<main>` per page.
- Lists are `<ul>`, `<ol>`, `<dl>` — not `<div>` groups.

### Keyboard

- Every interactive element is reachable by Tab.
- Tab order matches visual order.
- Focus state is **always** visible (no `outline: none` without an equivalent).
- Esc closes modals; Enter submits forms.
- Skip-link at the top of every layout to jump to main content.

### Forms

- Every `<input>` has an associated `<label>`.
- Required fields marked with `aria-required="true"` and a visible indicator.
- Error messages associated via `aria-describedby`.
- Form validation does not steal focus arbitrarily; it announces via `aria-live="polite"` regions.

### Color and Contrast

- Body text contrast ratio ≥ 4.5:1.
- Large text ≥ 3:1.
- UI components and graphical objects ≥ 3:1.
- Never rely on color alone to convey meaning; pair with text, icon, or shape.
- Tenant theme tokens require contrast validation before saving. G16(d), Accepted
  on 2026-10-02, requires the first whole-theme command to refuse a failing pair
  before saving. P02d-2 delivers this guard and its regression proofs. Its
  [complete palette contract](../modules/tenancy/README.md#whole-theme-setting-and-public-boundary)
  defines supported usage and atomic replacement. The future Studio can explain
  that refusal; a warning does not authorize saving an invalid palette.
- P02d-6 uses foreground plus underline for normal text links. Primary has only
  a 3:1 background guarantee; do not assume white text on primary meets body-text
  contrast. Primary may supply sufficient-contrast non-text focus/border accents.

### Images and Media

- `alt` attribute on every `<img>` content image. Decorative images use `alt=""`.
- Complex images (charts, diagrams) include a longer description (caption, `aria-describedby`, or accessible text alongside).
- Videos include captions or transcripts.
- Audio includes a transcript.
- Auto-playing media is allowed only when silent and with a pause control.

### ARIA

- Use semantic HTML first; ARIA only when HTML cannot express the intent.
- No "ARIA overuse." A button with `role="button"` is wrong; just use `<button>`.
- `aria-live` regions for dynamic content updates (toasts, validation).

### Live Classroom Specifics

- Camera and microphone toggles operable by keyboard.
- Mute / unmute announced via `aria-live`.
- Recording indicator is text + icon + color.
- Captions surface (when transcription exists post-MVP) is part of the AA baseline.

### Motion

- Respect `prefers-reduced-motion`.
- Avoid auto-playing animations longer than 5 s.
- No flashing content above 3 Hz.

### Time-Based Content

- Auto-logout warnings appear with sufficient lead time and an option to extend.
- Long-form learning content does not impose hard time limits unless pedagogically required.

## Tooling

- `eslint-plugin-jsx-a11y` in the frontend lint config.
- Phase 06 integrates `axe-core` with Playwright for public-renderer and portal
  critical flows.
- P02d-7 owns Lighthouse activation and the accessibility assertions for public
  routes; the CI placeholder remains disabled.
- Manual keyboard walkthroughs for new screens in the PR review.

**Accepted P02d-6 G43 — 2026-10-09; implementation pending.** Applicable
jsx-a11y rules run at error severity with planted controls through the actual app
configuration. Actual product-page HTML/DOM proves titles, languages, one main,
one descriptive h1, sequential headings, semantic lists/definition lists, skip
target and visible focus, including all controlled states. Logical CSS and wrapping
must support RTL and long unbroken strings. Acceptance does not promote this
standard: it stays **Adopted** until its enforcement lands.

Catalog → course → lesson is a critical flow requiring manual keyboard, focus,
320 CSS px reflow/zoom, long-string, contrast and real screen-reader smoke evidence.
The record names commit, environment, both hosts/locales and screen reader used.
DOM assertions cannot replace assistive-technology observation; pending evidence
keeps packet completion pending. Full Playwright/axe remains
[Phase 06](../roadmap/phase-06-renderer-admin-studio.md)'s, per
[Testing Standards](06-testing.md#end-to-end-tests). Accepted G44 leaves Lighthouse
activation and its assertions with P02d-7 after the P6 pages; no audit is passing
by acceptance alone.

## Testing

- Keyboard navigation for new screens.
- Screen reader smoke test on critical flows (VoiceOver, NVDA).
- Color contrast verified for new design tokens.
- `axe` baseline must not regress.

## Process

- Every PR with UI changes confirms accessibility in the PR description.
- Major flows undergo manual accessibility review before public launch.
- Tenant onboarding includes a checklist for accessibility of their content (alt text, headings).

## Forbidden

- Removing `outline` without replacing the focus state.
- Using `<div onClick>` instead of `<button>`.
- Placeholder text as a substitute for `<label>`.
- Tab traps in modals (focus must be returnable to the trigger on close).
- Disabling zoom or fixing viewport scale.
- Conveying meaning by color alone.
- Auto-playing audio.
