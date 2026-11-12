// @vitest-environment node
import { createHash, KeyObject, sign } from 'node:crypto';

import { exportJWK, generateKeyPair, SignJWT } from 'jose';
import { beforeAll, describe, expect, it, vi } from 'vitest';

import { createLogoutTokenVerifier } from './backchannel-verifier.js';

const issuer = 'https://identity.example.test/realms/learnstack';
const event = 'http://schemas.openid.net/event/backchannel-logout';
const now = 1_750_000_000_000;
let key: Awaited<ReturnType<typeof generateKeyPair>>;
let other: Awaited<ReturnType<typeof generateKeyPair>>;
beforeAll(async () => {
  key = await generateKeyPair('RS256');
  other = await generateKeyPair('RS256');
});

async function fixture() {
  const publicKey = await exportJWK(key.publicKey);
  const transport: typeof fetch = vi.fn(async (input) =>
    String(input).endsWith('/certs')
      ? Response.json({ keys: [{ ...publicKey, kid: 'key', alg: 'RS256', use: 'sig' }] })
      : Response.json({ issuer, jwks_uri: `${issuer}/protocol/openid-connect/certs` }),
  );
  const verifier = createLogoutTokenVerifier({ issuer }, transport, () => now);
  async function token(
    patch: Record<string, unknown> = {},
    header: Record<string, unknown> = {},
    signingKey = key.privateKey,
  ) {
    return new SignJWT({
      iss: issuer,
      aud: 'learnstack-web',
      iat: now / 1000,
      exp: now / 1000 + 120,
      sid: 'session',
      sub: 'subject',
      jti: 'event-id',
      events: { [event]: {} },
      ...patch,
    })
      .setProtectedHeader({ alg: 'RS256', typ: 'logout+jwt', kid: 'key', ...header })
      .sign(signingKey);
  }
  return { transport, verifier, token };
}

describe('native logout token verification', () => {
  it('verifies real signatures and returns bounded, immutable identity and digest', async () => {
    const f = await fixture();
    const token = await f.token();
    const result = await f.verifier.verify(token);
    expect(result).toEqual({
      issuer,
      sid: 'session',
      subject: 'subject',
      jti: 'event-id',
      fingerprint: createHash('sha256').update(token).digest('hex'),
      issuedAt: now,
      expiresAt: now + 120_000,
    });
    expect(Object.isFrozen(result)).toBe(true);
    await f.verifier.verify(token);
    expect(f.transport).toHaveBeenCalledTimes(2);
  });
  it.each([
    [{ iss: `${issuer}/other` }, {}],
    [{ aud: ['learnstack-web', 'other'] }, {}],
    [{ iat: now / 1000 - 301, exp: now / 1000 - 1 }, {}],
    [{ iat: now / 1000 + 31 }, {}],
    [{ iat: now / 1000 - 151, exp: now / 1000 - 31 }, {}],
    [{ exp: now / 1000 + 301 }, {}],
    [{ iat: 1.5 }, {}],
    [{ sid: '' }, {}],
    [{ sid: 'x'.repeat(256) }, {}],
    [{ jti: 'a\n' }, {}],
    [{ sub: null }, {}],
    [{ events: {} }, {}],
    [{ events: { [event]: { extra: true } } }, {}],
    [{ nonce: null }, {}],
    [{}, { typ: 'JWT' }],
    [{}, { jku: 'https://attacker.invalid/keys' }],
  ])('refuses invalid claims or purpose %#', async (patch, header) => {
    const f = await fixture();
    const token = await f.token(patch, header);
    await expect(f.verifier.verify(token)).rejects.toMatchObject({ reason: 'response' });
  });
  it('rejects a different key and bounds unknown-key refreshes', async () => {
    const f = await fixture();
    await expect(f.verifier.verify(await f.token({}, {}, other.privateKey))).rejects.toMatchObject({
      reason: 'response',
    });
    const unknown = await f.token({}, { kid: 'unknown' });
    await expect(f.verifier.verify(unknown)).rejects.toMatchObject({ reason: 'response' });
    await expect(f.verifier.verify(unknown)).rejects.toMatchObject({ reason: 'response' });
    expect(f.transport).toHaveBeenCalledTimes(4);
  });
  it('rejects duplicate escaped JSON names even when the signature is valid', async () => {
    const f = await fixture();
    const payload = `{"iss":"${issuer}","aud":"learnstack-web","iat":${now / 1000},"exp":${now / 1000 + 120},"sid":"session","s\\u0069d":"session","jti":"event-id","events":{"${event}":{}}}`;
    const header = Buffer.from(
      JSON.stringify({ alg: 'RS256', typ: 'logout+jwt', kid: 'key' }),
    ).toString('base64url');
    const input = `${header}.${Buffer.from(payload).toString('base64url')}`;
    const token = `${input}.${sign('RSA-SHA256', Buffer.from(input), KeyObject.from(key.privateKey)).toString('base64url')}`;
    await expect(f.verifier.verify(token)).rejects.toMatchObject({ reason: 'response' });
    expect(f.transport).not.toHaveBeenCalled();
  });
  it('rejects duplicate protected headers and malformed compact input before I/O', async () => {
    const f = await fixture();
    const payload = Buffer.from('{}').toString('base64url');
    const header = Buffer.from(
      '{"alg":"RS256","a\\u006cg":"RS256","typ":"logout+jwt","kid":"key"}',
    ).toString('base64url');
    await expect(f.verifier.verify(`${header}.${payload}.AA`)).rejects.toMatchObject({
      reason: 'response',
    });
    await expect(f.verifier.verify('a.b.c.d.e')).rejects.toMatchObject({ reason: 'response' });
    const parts = (await f.token()).split('.');
    parts[0] = Buffer.from(
      JSON.stringify({ alg: 'HS256', typ: 'logout+jwt', kid: 'key' }),
    ).toString('base64url');
    await expect(f.verifier.verify(parts.join('.'))).rejects.toMatchObject({ reason: 'response' });
    expect(f.transport).not.toHaveBeenCalled();
  });
  it('single-flights key retrieval and holds admission until actual transport settles', async () => {
    const f = await fixture();
    let release!: () => void;
    const held = new Promise<void>((resolve) => {
      release = resolve;
    });
    const transport: typeof fetch = vi.fn(async (input, init) => {
      await held;
      return f.transport(input, init);
    });
    const verifier = createLogoutTokenVerifier({ issuer }, transport, () => now);
    const token = await f.token();
    const attempts = Array.from({ length: 8 }, () => verifier.verify(token));
    await expect(verifier.verify(token)).rejects.toMatchObject({ reason: 'response' });
    expect(transport).toHaveBeenCalledTimes(1);
    release();
    await expect(Promise.all(attempts)).resolves.toHaveLength(8);
    expect(transport).toHaveBeenCalledTimes(2);
  });
  it('rechecks token age after slow provider work', async () => {
    const f = await fixture();
    let time = now;
    const transport: typeof fetch = async (input, init) => {
      const response = await f.transport(input, init);
      if (String(input).endsWith('/certs')) time = now + 301_000;
      return response;
    };
    const verifier = createLogoutTokenVerifier({ issuer }, transport, () => time);
    await expect(verifier.verify(await f.token({ exp: now / 1000 + 300 }))).rejects.toMatchObject({
      reason: 'response',
    });
  });
  it.each([
    ['zero lifetime', 0, 0, false],
    ['negative lifetime', 1, 0, false],
    ['maximum lifetime', 0, 300, true],
    ['excess lifetime', 0, 301, false],
    ['maximum age', -300, 0, true],
    ['excess age', -301, -1, false],
    ['future skew boundary', 30, 150, true],
    ['excess future skew', 31, 151, false],
    ['expiration skew boundary', -150, -30, true],
    ['excess expiration skew', -151, -31, false],
  ] as const)('enforces the logout time profile: %s', async (_name, iat, exp, accepted) => {
    const f = await fixture();
    const token = await f.token({ iat: now / 1000 + iat, exp: now / 1000 + exp });
    if (accepted) await expect(f.verifier.verify(token)).resolves.toHaveProperty('issuedAt');
    else await expect(f.verifier.verify(token)).rejects.toMatchObject({ reason: 'response' });
  });
  it('fails closed on clock rollback and invalid issuer before I/O', async () => {
    const f = await fixture();
    let time = now;
    const verifier = createLogoutTokenVerifier({ issuer }, f.transport, () => time);
    const token = await f.token();
    await verifier.verify(token);
    time--;
    await expect(verifier.verify(token)).rejects.toMatchObject({ reason: 'response' });
    expect(() =>
      createLogoutTokenVerifier({ issuer: 'http://example.test/realms/learnstack' }, f.transport),
    ).toThrow();
  });
});
