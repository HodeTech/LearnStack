import { COMPOSITE_KEYS, type CompositeKey } from './composites';
import { PRIMITIVE_KEYS, type PrimitiveKey } from './primitives';

/**
 * Runtime resolver — turns a tenant-supplied renderer key string into a known
 * primitive or composite key. Anything outside the closed sets resolves to
 * `null` and the caller renders a safe fallback (sanitized "unknown block").
 *
 * This is intentionally data-driven; per ADR-0018 there are NO domain-flavoured
 * renderer keys (`english.vocabulary-list`, `yoga.asana-card`, …). Tenants
 * point at composites via their `TenantPageBlock` row and let the JSON Schema
 * + data path do the work.
 */
export type ResolvedRenderer =
  | { readonly kind: 'primitive'; readonly key: PrimitiveKey }
  | { readonly kind: 'composite'; readonly key: CompositeKey }
  | null;

export function resolveRendererKey(rawKey: string): ResolvedRenderer {
  if ((PRIMITIVE_KEYS as readonly string[]).includes(rawKey)) {
    return { kind: 'primitive', key: rawKey as PrimitiveKey };
  }
  if ((COMPOSITE_KEYS as readonly string[]).includes(rawKey)) {
    return { kind: 'composite', key: rawKey as CompositeKey };
  }
  return null;
}
