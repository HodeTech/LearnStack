/**
 * Composite renderer registry — the keys tenants reference from
 * `TenantPageBlock.renderer_key` / `TenantContentType.renderer_key`.
 *
 * The set is closed: a tenant requesting a truly novel UI either composes
 * primitives or asks LearnStack to add a new composite. Adding a key here is
 * a platform release, not a tenant action (ADR-0018 § Renderer architecture).
 */
export const COMPOSITE_KEYS = [
  'default-card',
  'content-list',
  'media-gallery',
  'rich-page',
] as const;

export type CompositeKey = (typeof COMPOSITE_KEYS)[number];

export type CompositeRendererProps = {
  readonly key: CompositeKey;
  readonly schema: unknown;
  readonly data: unknown;
};
