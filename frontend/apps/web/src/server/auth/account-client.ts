import { randomBytes } from 'node:crypto';

import { createAuthenticatedServerSdk } from '@learnstack/sdk/authenticated-server';

import { canonicalAddress, normalizeHost, validSecret } from '../ingress.js';
import { MAX_PUBLIC_RESPONSE_BYTES, PUBLIC_API_DEADLINE_MS } from '../public-api-limits.js';
import { BffProviderError } from './provider-error.js';
import type { SessionDependencies } from './session-contract.js';

type Configuration = Readonly<{ apiOrigin: string; secret: string }>;
const trace = /^00-([0-9a-f]{32})-([0-9a-f]{16})-[0-9a-f]{2}$/;
function traceparent(value: string | undefined) {
  const matched = value?.match(trace);
  return matched && matched[1] !== '0'.repeat(32) && matched[2] !== '0'.repeat(16)
    ? value!
    : '00-' + randomBytes(16).toString('hex') + '-' + randomBytes(8).toString('hex') + '-00';
}

/** Native configured AccountSite capability; no UI-facing credential/header options. */
export function createAccountSiteCaller(
  configuration: Configuration,
  transport: typeof fetch,
): SessionDependencies['account'] {
  let origin: URL;
  try {
    origin = new URL(configuration.apiOrigin);
  } catch {
    throw new BffProviderError('configuration');
  }
  const loopback =
    origin.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(origin.hostname);
  if (
    origin.origin !== configuration.apiOrigin ||
    (!loopback && origin.protocol !== 'https:') ||
    !validSecret(configuration.secret)
  )
    throw new BffProviderError('configuration');
  const apiOrigin = origin.origin;
  const secret = configuration.secret;
  return async (accessToken, binding, caller) => {
    if (
      normalizeHost(binding.host) !== binding.host ||
      canonicalAddress(binding.source) !== binding.source ||
      typeof accessToken !== 'string' ||
      !accessToken ||
      /[\r\n]/.test(accessToken)
    )
      throw new BffProviderError('configuration');
    const deadline = AbortSignal.timeout(PUBLIC_API_DEADLINE_MS);
    const signal = AbortSignal.any([caller, deadline]);
    const interrupted = () => {
      if (signal.aborted) throw new BffProviderError(caller.aborted ? 'cancelled' : 'deadline');
    };
    let failure: BffProviderError | undefined;
    const sdk = createAuthenticatedServerSdk(async (path, init) => {
      let reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
      try {
        interrupted();
        if (path !== '/api/v1/account/site' || init.method !== 'GET')
          throw new BffProviderError('configuration');
        const response = await transport(apiOrigin + path, {
          method: 'GET',
          cache: 'no-store',
          credentials: 'omit',
          redirect: 'error',
          signal,
          headers: {
            Accept: 'application/json',
            Authorization: 'Bearer ' + accessToken,
            'X-LearnStack-Host': binding.host,
            'X-LearnStack-Hop-Secret': secret,
            'X-LearnStack-Visitor-Address': binding.source,
            traceparent: traceparent(binding.traceparent),
          },
        });
        reader = response.body?.getReader();
        interrupted();
        if (response.redirected || (response.status >= 300 && response.status < 400))
          throw new BffProviderError('response');
        const media = response.headers.get('content-type')?.split(';', 1)[0]?.trim().toLowerCase();
        if (media !== (response.status === 200 ? 'application/json' : 'application/problem+json'))
          throw new BffProviderError('response');
        const chunks: Uint8Array[] = [];
        let bytes = 0;
        while (reader) {
          const chunk = await reader.read();
          interrupted();
          if (chunk.done) break;
          bytes += chunk.value.byteLength;
          if (bytes > MAX_PUBLIC_RESPONSE_BYTES) throw new BffProviderError('response');
          chunks.push(chunk.value);
        }
        const body = new Uint8Array(bytes);
        let offset = 0;
        for (const chunk of chunks) {
          body.set(chunk, offset);
          offset += chunk.byteLength;
        }
        return new Response([204, 205, 304].includes(response.status) ? null : body, {
          status: response.status,
          headers: {
            'content-type': media!,
            ...(response.headers.has('retry-after')
              ? { 'retry-after': response.headers.get('retry-after')! }
              : {}),
          },
        });
      } catch (error) {
        interrupted();
        failure = error instanceof BffProviderError ? error : new BffProviderError('transport');
        throw failure;
      } finally {
        await reader?.cancel().catch(() => {});
        reader?.releaseLock();
      }
    });
    const result = await sdk.getAccountSite({ signal });
    interrupted();
    if (failure) throw failure;
    // API 401 includes undecidable credentials. Only its deliberate 403/404
    // access-boundary refusals select the session owner's identity category.
    if (result.kind !== 'success')
      throw new BffProviderError(
        result.kind === 'api-error' && [403, 404].includes(result.status)
          ? 'identity'
          : result.kind === 'api-error'
            ? 'provider_refused'
            : result.kind === 'transport-error'
              ? 'transport'
              : 'response',
      );
    return Object.freeze({
      actorId: result.data.actorId.toLowerCase(),
      tenantId: result.data.tenantId.toLowerCase(),
      organizationId: result.data.organizationId?.toLowerCase() ?? null,
      displayName: result.data.site.displayName,
    });
  };
}
