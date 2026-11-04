/** Synchronous test-owned proof boundary; no caught data or cause is exported. */
export function closedFrameworkProofSync<T>(operation: () => T): T {
  try {
    return operation();
  } catch {
    throw new Error('Framework proof refused');
  }
}
