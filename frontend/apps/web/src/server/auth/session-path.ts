import type { NativeReturnPath } from './session-contract.js';

/** Canonical local screens only; queries, fragments and arbitrary paths never return authority. */
export function isNativeReturnPath(value: unknown): value is NativeReturnPath {
  return (
    typeof value === 'string' &&
    (['/', '/studio', '/studio/members', '/studio/roles', '/portal', '/portal/profile'].includes(
      value,
    ) ||
      /^\/studio\/(?:members|roles)\/[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(
        value,
      ))
  );
}
