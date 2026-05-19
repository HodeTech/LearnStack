/**
 * ADR-0018 § Renderer architecture defines a **closed set** of generic primitives.
 * Adding a new primitive is a LearnStack release (CODEOWNERS rule), not a tenant
 * action. Tenants compose these via `TenantPageBlock` / `TenantContentType` data.
 *
 * The architecture test `Generic_Primitives_Only_In_Renderer` (Phase 02a) freezes
 * this set against drift.
 */
export const PRIMITIVE_KEYS = [
  'text',
  'markdown',
  'image',
  'video',
  'audio',
  'pdf',
  'code',
  'math',
  'link',
  'list',
  'tabs',
  'embed-html',
] as const;

export type PrimitiveKey = (typeof PRIMITIVE_KEYS)[number];

export type PrimitiveProps = {
  readonly key: PrimitiveKey;
  readonly value: unknown;
};
