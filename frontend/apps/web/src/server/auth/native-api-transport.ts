import { randomBytes } from 'node:crypto';

import { canonicalAddress, normalizeHost, validSecret } from '../ingress.js';
import { NativeApiError } from './native-contract.js';
import type { NativeBinding, NativeScope } from './native-contract.js';

export const NATIVE_API_MAX_BYTES = 256 * 1024;
export const NATIVE_API_DEADLINE_MS = 10_000;
export type NativeApiConfiguration = Readonly<{
  apiOrigin: string;
  secret: string;
  nativeSourceSecret: string;
}>;
const trace = /^00-([0-9a-f]{32})-([0-9a-f]{16})-[0-9a-f]{2}$/;
export function canonicalUuid(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value) &&
    value !== '00000000-0000-0000-0000-000000000000'
  );
}
export function nativeScope(value: NativeScope): boolean {
  return (
    canonicalUuid(value.tenantId) &&
    value.tenantId !== '00000000-0000-7000-8000-000000000002' &&
    (value.organizationId === null || canonicalUuid(value.organizationId))
  );
}
export function wireObject(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}
export function exactFields(
  value: unknown,
  fields: readonly string[],
): value is Record<string, unknown> {
  return (
    wireObject(value) &&
    Object.keys(value).length === fields.length &&
    fields.every((field) => Object.hasOwn(value, field))
  );
}
export function storableText(value: unknown, maximum: number): value is string {
  if (
    typeof value !== 'string' ||
    value.length === 0 ||
    value.length > maximum ||
    /^\p{White_Space}+$/u.test(value)
  )
    return false;
  for (const point of value) {
    const code = point.codePointAt(0)!;
    if (code === 0 || (code >= 0xd800 && code <= 0xdfff)) return false;
  }
  return true;
}

/** Configured native transport. Callers supply only plans built by the closed clients. */
export function createNativeApiTransport(
  configuration: NativeApiConfiguration,
  transport: typeof fetch,
) {
  let origin: URL;
  try {
    origin = new URL(configuration.apiOrigin);
  } catch {
    throw new NativeApiError('configuration');
  }
  const loopback =
    origin.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(origin.hostname);
  const native = configuration.nativeSourceSecret;
  const decoded =
    typeof native === 'string' && /^[A-Za-z0-9_-]{43}$/.test(native)
      ? Buffer.from(native, 'base64url')
      : null;
  if (
    origin.origin !== configuration.apiOrigin ||
    (!loopback && origin.protocol !== 'https:') ||
    !validSecret(configuration.secret) ||
    !decoded ||
    decoded.length !== 32 ||
    decoded.toString('base64url') !== native ||
    decoded.every((byte) => byte === decoded[0]) ||
    native === configuration.secret
  )
    throw new NativeApiError('configuration');
  const apiOrigin = origin.origin;
  const secret = configuration.secret;
  const nativeSourceSecret = native;
  return async (
    token: string,
    binding: NativeBinding,
    scope: NativeScope | null,
    path: string,
    init: RequestInit,
    caller: AbortSignal,
  ): Promise<Response> => {
    if (
      normalizeHost(binding.host) !== binding.host ||
      canonicalAddress(binding.source) !== binding.source ||
      typeof token !== 'string' ||
      !token ||
      /[^\x21-\x7e]/.test(token) ||
      (scope !== null && !nativeScope(scope)) ||
      !path.startsWith('/api/v1/') ||
      path.includes('#') ||
      !['GET', 'PUT', 'POST'].includes(init.method ?? '')
    )
      throw new NativeApiError('configuration');
    const deadline = AbortSignal.timeout(NATIVE_API_DEADLINE_MS);
    const signal = AbortSignal.any([caller, deadline]);
    const interrupted = () => {
      if (signal.aborted) throw new NativeApiError(caller.aborted ? 'cancelled' : 'deadline');
    };
    const outgoing = new Headers(init.headers);
    for (const [name] of outgoing)
      if (!['accept', 'content-type', 'idempotency-key', 'if-match'].includes(name))
        throw new NativeApiError('configuration');
    outgoing.set('Accept', 'application/json');
    outgoing.set('Authorization', 'Bearer ' + token);
    outgoing.set('X-LearnStack-Host', binding.host);
    outgoing.set('X-LearnStack-Hop-Secret', secret);
    outgoing.set('X-LearnStack-Native-Session-Secret', nativeSourceSecret);
    outgoing.set('X-LearnStack-Visitor-Address', binding.source);
    const match = binding.traceparent?.match(trace);
    outgoing.set(
      'traceparent',
      match && match[1] !== '0'.repeat(32) && match[2] !== '0'.repeat(16)
        ? binding.traceparent!
        : '00-' + randomBytes(16).toString('hex') + '-' + randomBytes(8).toString('hex') + '-00',
    );
    if (scope !== null) {
      outgoing.set('X-LearnStack-Native-Tenant-Id', scope.tenantId);
      if (scope.organizationId !== null)
        outgoing.set('X-LearnStack-Native-Organization-Id', scope.organizationId);
    }
    let reader: ReadableStreamDefaultReader<Uint8Array> | undefined;
    let cancellation: Promise<void> | undefined;
    const cancel = () => {
      if (reader) cancellation ??= reader.cancel().catch(() => undefined);
    };
    try {
      interrupted();
      const response = await transport(apiOrigin + path, {
        method: init.method,
        body: init.body,
        headers: outgoing,
        cache: 'no-store',
        credentials: 'omit',
        redirect: 'error',
        signal,
      });
      reader = response.body?.getReader();
      signal.addEventListener('abort', cancel, { once: true });
      if (signal.aborted) cancel();
      interrupted();
      const media = response.headers.get('content-type')?.split(';', 1)[0]?.trim().toLowerCase();
      if (
        response.redirected ||
        (response.status < 400 && ![200, 201].includes(response.status)) ||
        media !== (response.status < 400 ? 'application/json' : 'application/problem+json')
      )
        throw new NativeApiError('response');
      const chunks: Uint8Array[] = [];
      let bytes = 0;
      while (reader) {
        const chunk = await reader.read();
        interrupted();
        if (chunk.done) break;
        bytes += chunk.value.byteLength;
        if (bytes > NATIVE_API_MAX_BYTES) throw new NativeApiError('response');
        chunks.push(chunk.value);
      }
      const body = new Uint8Array(bytes);
      let offset = 0;
      for (const chunk of chunks) {
        body.set(chunk, offset);
        offset += chunk.byteLength;
      }
      const headers = new Headers({ 'content-type': media! });
      for (const name of [
        'ETag',
        'Location',
        'Idempotency-Replayed',
        'Retry-After',
        'Cache-Control',
      ]) {
        const value = response.headers.get(name);
        if (value !== null) headers.set(name, value);
      }
      return new Response(body, { status: response.status, headers });
    } catch (error) {
      interrupted();
      throw error instanceof NativeApiError ? error : new NativeApiError('transport');
    } finally {
      signal.removeEventListener('abort', cancel);
      cancel();
      if (cancellation) await cancellation;
      reader?.releaseLock();
    }
  };
}
