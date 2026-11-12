import { createHash } from 'node:crypto';

import { compactVerify, createLocalJWKSet, errors } from 'jose';

import type { VerifiedLogoutToken } from './backchannel-contract.js';
import { BffProviderError } from './provider-error.js';
import { providerOperation } from './provider-transport.js';

const event = 'http://schemas.openid.net/event/backchannel-logout';
const maxTokenBytes = 16 * 1024;
const cacheMs = 300_000;
const unknownRefreshMs = 30_000;

type JsonObject = Record<string, unknown>;

class LogoutVerificationUnavailable extends BffProviderError {
  constructor(readonly stage: 'provider' | 'clock' | 'capacity') {
    super('response');
  }
}

/** JSON.parse alone silently accepts duplicate names, including escaped equivalents. */
function uniqueJson(text: string): unknown {
  let pos = 0;
  function space() {
    while (/[\x20\t\r\n]/u.test(text[pos] ?? '') && pos < text.length) pos++;
  }
  function string(): string {
    const start = pos++;
    while (pos < text.length) {
      if (text[pos] === '"') return JSON.parse(text.slice(start, ++pos)) as string;
      if (text[pos++] === '\\') pos++;
    }
    throw new Error();
  }
  function value(depth: number): void {
    if (depth > 32) throw new Error();
    space();
    if (text[pos] === '"') {
      string();
      return;
    }
    if (text[pos] === '{') {
      pos++;
      space();
      const names = new Set<string>();
      if (text[pos] === '}') {
        pos++;
        return;
      }
      for (;;) {
        space();
        if (text[pos] !== '"') throw new Error();
        const name = string();
        if (names.has(name)) throw new Error();
        names.add(name);
        space();
        if (text[pos++] !== ':') throw new Error();
        value(depth + 1);
        space();
        const separator = text[pos++];
        if (separator === '}') return;
        if (separator !== ',') throw new Error();
      }
    }
    if (text[pos] === '[') {
      pos++;
      space();
      if (text[pos] === ']') {
        pos++;
        return;
      }
      for (;;) {
        value(depth + 1);
        space();
        const separator = text[pos++];
        if (separator === ']') return;
        if (separator !== ',') throw new Error();
      }
    }
    const match = /^(?:true|false|null|-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?)/u.exec(
      text.slice(pos),
    );
    if (!match) throw new Error();
    pos += match[0].length;
  }
  value(0);
  space();
  if (pos !== text.length) throw new Error();
  return JSON.parse(text) as unknown;
}

function object(value: unknown): value is JsonObject {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}
function opaque(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    value.length > 0 &&
    Buffer.byteLength(value, 'utf8') <= 255 &&
    // eslint-disable-next-line no-control-regex -- Reject control bytes in opaque provider identifiers.
    !/[\u0000-\u001f\u007f-\u009f\ud800-\udfff]/u.test(value)
  );
}
function subject(value: unknown): value is string {
  return (
    typeof value === 'string' &&
    value.length > 0 &&
    value.length <= 255 &&
    !/^[\t\n\v\f\r \u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+$/u.test(value) &&
    !/[\0\r\n\ud800-\udfff]/u.test(value)
  );
}
function decode(part: string): unknown {
  if (!/^[A-Za-z0-9_-]+$/u.test(part)) throw new Error();
  const bytes = Buffer.from(part, 'base64url');
  if (bytes.toString('base64url') !== part) throw new Error();
  return uniqueJson(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
}

/** Native logout adapter. Its only network destinations are fixed by the issuer. */
export function createLogoutTokenVerifier(
  input: Readonly<{ issuer: string; allowLoopbackHttp?: boolean }>,
  transport: typeof fetch,
  clock: () => number = Date.now,
) {
  let url: URL;
  try {
    url = new URL(input.issuer);
  } catch {
    throw new BffProviderError('configuration');
  }
  const insecure =
    url.protocol === 'http:' &&
    input.allowLoopbackHttp === true &&
    ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname);
  if (
    (!insecure && url.protocol !== 'https:') ||
    url.pathname !== '/realms/learnstack' ||
    url.href !== input.issuer ||
    url.search ||
    url.hash ||
    url.username ||
    url.password
  )
    throw new BffProviderError('configuration');
  const issuer = url.href;
  let cached: ReturnType<typeof createLocalJWKSet> | undefined;
  let cachedAt = 0;
  let lastUnknownRefresh = -Infinity;
  let lastClock = -Infinity;
  let pending: Promise<ReturnType<typeof createLocalJWKSet>> | undefined;
  let active = 0;

  async function keys(signal: AbortSignal | undefined, now: number) {
    if (pending) {
      try {
        return await pending;
      } catch {
        throw new LogoutVerificationUnavailable('provider');
      }
    }
    const operation = providerOperation(issuer, signal, transport);
    pending = (async () => {
      const discovery = await operation.fetch(`${issuer}/.well-known/openid-configuration`);
      if (discovery.status !== 200) throw new BffProviderError('provider_refused');
      const metadata: unknown = uniqueJson(await discovery.text());
      if (
        !object(metadata) ||
        metadata.issuer !== issuer ||
        metadata.jwks_uri !== `${issuer}/protocol/openid-connect/certs`
      )
        throw new BffProviderError('configuration');
      const response = await operation.fetch(`${issuer}/protocol/openid-connect/certs`);
      if (response.status !== 200) throw new BffProviderError('provider_refused');
      const jwks: unknown = uniqueJson(await response.text());
      if (
        !object(jwks) ||
        !Array.isArray(jwks.keys) ||
        jwks.keys.length < 1 ||
        jwks.keys.length > 32 ||
        jwks.keys.some(
          (key: unknown) =>
            !object(key) ||
            typeof key.kty !== 'string' ||
            ['d', 'p', 'q', 'dp', 'dq', 'qi', 'oth', 'k'].some((name) => name in key),
        )
      )
        throw new BffProviderError('response');
      const publicKeys = jwks.keys.map((key: unknown) => {
        if (!object(key) || typeof key.kty !== 'string') throw new BffProviderError('response');
        return { ...key, kty: key.kty };
      });
      const result = createLocalJWKSet({ keys: publicKeys });
      operation.interrupted();
      cached = result;
      cachedAt = now;
      return result;
    })();
    try {
      return await pending;
    } catch {
      throw new LogoutVerificationUnavailable('provider');
    } finally {
      pending = undefined;
    }
  }

  return Object.freeze({
    async verify(token: string, signal?: AbortSignal): Promise<VerifiedLogoutToken> {
      if (active >= 8) throw new LogoutVerificationUnavailable('capacity');
      active++;
      try {
        const now = clock();
        if (!Number.isSafeInteger(now) || now < lastClock)
          throw new LogoutVerificationUnavailable('clock');
        lastClock = now;
        if (signal?.aborted) throw new BffProviderError('cancelled');
        if (typeof token !== 'string' || Buffer.byteLength(token, 'utf8') > maxTokenBytes)
          throw new Error();
        const parts = token.split('.');
        if (parts.length !== 3 || !/^[A-Za-z0-9_-]+$/u.test(parts[2]!)) throw new Error();
        const header = decode(parts[0]!);
        const claims = decode(parts[1]!);
        if (
          !object(header) ||
          header.alg !== 'RS256' ||
          header.typ !== 'logout+jwt' ||
          typeof header.kid !== 'string' ||
          !opaque(header.kid) ||
          ['jku', 'jwk', 'x5u', 'x5c', 'crit'].some((name) => name in header) ||
          !object(claims)
        )
          throw new Error();
        const iat = claims.iat;
        const exp = claims.exp;
        if (
          claims.iss !== issuer ||
          !(
            claims.aud === 'learnstack-web' ||
            (Array.isArray(claims.aud) &&
              claims.aud.length === 1 &&
              claims.aud[0] === 'learnstack-web')
          ) ||
          !Number.isSafeInteger(iat) ||
          !Number.isSafeInteger(exp) ||
          typeof iat !== 'number' ||
          typeof exp !== 'number' ||
          exp - iat <= 0 ||
          exp - iat > 300 ||
          iat * 1000 > now + 30_000 ||
          iat * 1000 < now - 300_000 ||
          exp * 1000 < now - 30_000 ||
          !Number.isSafeInteger(iat * 1000) ||
          !Number.isSafeInteger(exp * 1000) ||
          !opaque(claims.sid) ||
          !opaque(claims.jti) ||
          ('sub' in claims && !subject(claims.sub)) ||
          'nonce' in claims ||
          !object(claims.events) ||
          !object(claims.events[event]) ||
          Object.keys(claims.events[event]).length !== 0
        )
          throw new Error();
        let keyset = cached && now - cachedAt < cacheMs ? cached : await keys(signal, now);
        try {
          await compactVerify(token, keyset, { algorithms: ['RS256'] });
        } catch (error) {
          if (!(error instanceof errors.JWKSNoMatchingKey)) throw error;
          if (pending) keyset = await keys(signal, now);
          else {
            if (now - lastUnknownRefresh < unknownRefreshMs) throw error;
            lastUnknownRefresh = now;
            keyset = await keys(signal, now);
          }
          await compactVerify(token, keyset, { algorithms: ['RS256'] });
        }
        if (signal?.aborted) throw new BffProviderError('cancelled');
        const finished = clock();
        if (
          !Number.isSafeInteger(finished) ||
          finished < lastClock ||
          iat * 1000 > finished + 30_000 ||
          iat * 1000 < finished - 300_000 ||
          exp * 1000 < finished - 30_000
        )
          throw new LogoutVerificationUnavailable('clock');
        lastClock = finished;
        return Object.freeze({
          issuer,
          sid: claims.sid,
          subject: claims.sub === undefined ? null : (claims.sub as string),
          jti: claims.jti,
          fingerprint: createHash('sha256').update(token).digest('hex'),
          issuedAt: iat * 1000,
          expiresAt: exp * 1000,
        });
      } catch (error) {
        if (error instanceof BffProviderError) throw error;
        throw new BffProviderError('response');
      } finally {
        active--;
      }
    },
  });
}
