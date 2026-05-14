# 16 — Accessibility Standards

**Status:** Active
**Derives from:** WCAG 2.2 AA (external authoritative standard), [00-principles.md](00-principles.md) § 6 (Foundation First).

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
- Tenant theme tokens are validated for contrast before saving (Admin Studio surfaces a warning).

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
- `axe-core` integrated with Playwright; runs on every E2E test for the public renderer and portal critical flows.
- Lighthouse accessibility audit runs in CI for public routes.
- Manual keyboard walkthroughs for new screens in the PR review.

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
