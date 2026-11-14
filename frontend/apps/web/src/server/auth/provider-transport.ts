import { BffProviderError } from './provider-error.js';

export const PROVIDER_DEADLINE_MS = 10_000;
export const MAX_PROVIDER_BODY_BYTES = 256 * 1024;

/** Private RP transport: one fixed issuer/purpose set, one deadline through body. */
export function providerOperation(
  issuer: string,
  caller: AbortSignal | undefined,
  transport: typeof fetch,
) {
  const deadline = AbortSignal.timeout(PROVIDER_DEADLINE_MS);
  const signal = caller ? AbortSignal.any([caller, deadline]) : deadline;
  const purposes = new Map([
    [`${issuer}/.well-known/openid-configuration`, 'GET'],
    [`${issuer}/protocol/openid-connect/token`, 'POST'],
    [`${issuer}/protocol/openid-connect/certs`, 'GET'],
    // ADR-0099 § Host binding: the server-side end of a provider session after a definitive mismatch.
    [`${issuer}/protocol/openid-connect/logout`, 'POST'],
  ]);
  const logout = `${issuer}/protocol/openid-connect/logout`;
  const interrupted = () => {
    if (signal.aborted) throw new BffProviderError(caller?.aborted ? 'cancelled' : 'deadline');
  };
  return {
    signal,
    interrupted,
    async fetch(input: string | URL | Request, init?: RequestInit): Promise<Response> {
      interrupted();
      const url = input instanceof Request ? input.url : String(input);
      if (purposes.get(url) !== (init?.method ?? 'GET').toUpperCase())
        throw new BffProviderError('configuration');
      let reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
      try {
        const response = await transport(url, {
          ...init,
          signal,
          redirect: 'error',
          credentials: 'omit',
          cache: 'no-store',
        });
        reader = response.body?.getReader();
        interrupted();
        if (response.redirected || (response.status >= 300 && response.status < 400))
          throw new BffProviderError('response');
        const media = response.headers.get('content-type')?.split(';', 1)[0]?.trim().toLowerCase();
        // The end-session form answers 204 with no body; every other purpose is JSON.
        if (
          !(url === logout && response.status === 204) &&
          media !== 'application/json' &&
          media !== 'application/jwk-set+json'
        )
          throw new BffProviderError('response');
        const chunks: Uint8Array[] = [];
        let length = 0;
        while (reader) {
          const part = await reader.read();
          interrupted();
          if (part.done) break;
          length += part.value.byteLength;
          if (length > MAX_PROVIDER_BODY_BYTES) throw new BffProviderError('response');
          chunks.push(part.value);
        }
        const bytes = new Uint8Array(length);
        let offset = 0;
        for (const chunk of chunks) {
          bytes.set(chunk, offset);
          offset += chunk.byteLength;
        }
        const headers = new Headers(response.headers);
        // Fetch supplies decoded bytes. Never describe them as the original encoding.
        headers.delete('content-encoding');
        headers.delete('content-length');
        // A 204 (the end-session answer) cannot carry even an empty body.
        if (response.status === 204) {
          if (length !== 0) throw new BffProviderError('response');
          return new Response(null, { status: 204, headers });
        }
        return new Response(bytes, { status: response.status, headers });
      } catch (error) {
        interrupted();
        throw error instanceof BffProviderError ? error : new BffProviderError('transport');
      } finally {
        await reader?.cancel().catch(() => {});
        reader?.releaseLock();
      }
    },
  };
}
