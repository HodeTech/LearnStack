# Extension Model

LearnStack should support vertical education products without changing the core every time.

## Extension Types

### Domain Extensions

Product-specific entities and workflows.

Examples:

- CEFR placement test for English education.
- Exam curriculum mapping for exam preparation.
- Corporate compliance training rules.

### Content Extensions

Product-specific content types and blocks.

Examples:

- Vocabulary list.
- Grammar unit.
- Instructor spotlight.
- Course comparison table.
- Placement test call-to-action.

### Integration Extensions

External provider adapters.

Examples:

- Payment providers.
- Live meeting providers.
- Email/SMS/WhatsApp providers.
- CRM providers.
- LTI/xAPI tools.

### UI Extensions

Tenant or product-specific frontend components.

Examples:

- Custom page blocks.
- Branded landing templates.
- Product-specific portal widgets.

## Extension Rules

- Core modules define stable primitives.
- Product modules add behavior through explicit extension points.
- Tenant configuration decides which capabilities are enabled.
- Provider-specific code stays behind adapters.
- Product-specific rules should not leak into generic core modules.

## Example: English Learning Product

Core provides:

- Tenant
- Page
- Course
- Lesson
- Assessment
- Enrollment
- Progress
- Instructor profile

English product adds:

- CEFR level taxonomy
- Placement test scoring rules
- Speaking session metadata
- Grammar topic taxonomy
- Vocabulary bank
- Teacher matching rules
- Lesson package definitions

This keeps LearnStack reusable while still allowing rich vertical products.

