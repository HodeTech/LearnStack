import type { SessionProjection } from '@/server/auth/session-projection';

/** Browser → same-origin BFF only. This is not a backend/API transport. */
export async function extendBffSession(csrfToken: string): Promise<SessionProjection> {
  if (!/^[A-Za-z0-9_-]{43}$/.test(csrfToken)) throw new Error('Session unavailable');
  // eslint-disable-next-line no-restricted-globals -- Fixed BFF action, never the backend API.
  const response = await fetch('/api/auth/extend', {
    method: 'POST',
    credentials: 'same-origin',
    cache: 'no-store',
    redirect: 'error',
    headers: { 'content-type': 'application/x-www-form-urlencoded;charset=UTF-8' },
    body: new URLSearchParams({ csrfToken }).toString(),
    signal: AbortSignal.timeout(10_000),
  });
  if (!response.ok || !response.headers.get('content-type')?.startsWith('application/json'))
    throw new Error('Session unavailable');
  const reader = response.body?.getReader();
  if (!reader) throw new Error('Session unavailable');
  try {
    const chunks: Uint8Array[] = [];
    let size = 0;
    while (true) {
      const chunk = await reader.read();
      if (chunk.done) break;
      size += chunk.value.byteLength;
      if (size > 16_384) throw new Error('Session unavailable');
      chunks.push(chunk.value);
    }
    const body = new Uint8Array(size);
    let offset = 0;
    for (const chunk of chunks) {
      body.set(chunk, offset);
      offset += chunk.byteLength;
    }
    const value: unknown = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(body));
    if (!value || typeof value !== 'object' || Array.isArray(value))
      throw new Error('Session unavailable');
    const session = value as Record<string, unknown>;
    if (
      Object.keys(session).sort().join(',') !==
        'csrfToken,displayName,expiresAt,limitedByAbsolute,state' ||
      session.state !== 'authenticated' ||
      typeof session.displayName !== 'string' ||
      typeof session.expiresAt !== 'number' ||
      !Number.isFinite(session.expiresAt) ||
      typeof session.limitedByAbsolute !== 'boolean' ||
      typeof session.csrfToken !== 'string' ||
      !/^[A-Za-z0-9_-]{43}$/.test(session.csrfToken)
    )
      throw new Error('Session unavailable');
    return Object.freeze(session as SessionProjection);
  } catch {
    throw new Error('Session unavailable');
  } finally {
    await reader.cancel().catch(() => {});
    reader.releaseLock();
  }
}
