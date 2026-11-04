// @vitest-environment node
import { createCipheriv, randomBytes } from 'node:crypto';

import { describe, expect, it } from 'vitest';

import { openEntryCursor, sealEntryCursor } from './entry-cursor';
import { BffInputError } from './input-error';
import { BffSessionError } from './session-contract';

const binding = {
  id: 'entry-owner-only',
  host: 'institution.example.test:3000',
  epoch: 'current-group-epoch',
};
const purpose = 'learnstack.identity.self-context-cursor';
const raw = Buffer.from(
  '10000000-0000-4000-8000-000000000099:' +
    'b'.repeat(64) +
    ':019a154b-0000-7000-8000-000000000011',
).toString('base64url');

// Independently construct authenticated counterexamples: a valid tag under an
// unrelated purpose/version must not inherit the production reader's contract.
function authenticated(
  key: Buffer,
  plaintext: Buffer,
  associated: readonly unknown[],
  tagLength: 8 | 12 | 16 = 16,
) {
  const iv = randomBytes(12);
  const cipher = createCipheriv('aes-256-gcm', key, iv, { authTagLength: tagLength });
  cipher.setAAD(Buffer.from(JSON.stringify(associated)));
  return Buffer.concat([
    Buffer.from([1]),
    iv,
    cipher.update(plaintext),
    cipher.final(),
    cipher.getAuthTag(),
  ]).toString('base64url');
}

describe('entry cursor authenticated confidentiality', () => {
  it('uses fresh 12-byte IVs and full tags while keeping plaintext and key outside the envelope', () => {
    const key = randomBytes(32);
    const retained = Buffer.from(key);
    const wrapped = Array.from({ length: 16 }, () => sealEntryCursor(key, binding, raw));
    const envelopes = wrapped.map((value) => Buffer.from(value, 'base64url'));
    expect(new Set(wrapped).size).toBe(16);
    expect(new Set(envelopes.map((value) => value.subarray(1, 13).toString('hex'))).size).toBe(16);
    for (const [index, envelope] of envelopes.entries()) {
      expect(envelope[0]).toBe(1);
      expect(envelope.length).toBe(1 + 12 + Buffer.byteLength(raw) + 16);
      expect(envelope.includes(Buffer.from(raw))).toBe(false);
      expect(envelope.includes(key)).toBe(false);
      expect(wrapped[index]).toMatch(/^[A-Za-z0-9_-]+$/);
      expect(openEntryCursor(key, binding, wrapped[index]!)).toBe(raw);
    }
    expect(key.equals(retained)).toBe(true);
  });

  it.each(['id', 'host', 'epoch'] as const)(
    'rejects another %s with the same key, isolating that associated-data dimension',
    (dimension) => {
      const key = randomBytes(32);
      const cursor = sealEntryCursor(key, binding, raw);
      expect(() =>
        openEntryCursor(
          key,
          { ...binding, [dimension]: 'different-' + binding[dimension] },
          cursor,
        ),
      ).toThrow(BffInputError);
      expect(openEntryCursor(key, binding, cursor)).toBe(raw);
    },
  );

  it.each([undefined, 'members', 'scopes'] as const)(
    'isolates the %s cursor purpose from both other browser flows with the same owner and key',
    (selected) => {
      const key = randomBytes(32);
      const owner = selected === undefined ? binding : { ...binding, purpose: selected };
      const cursor = sealEntryCursor(key, owner, raw);
      expect(openEntryCursor(key, owner, cursor)).toBe(raw);
      for (const other of [undefined, 'members', 'scopes'] as const) {
        if (other === selected) continue;
        const otherOwner = other === undefined ? binding : { ...binding, purpose: other };
        expect(() => openEntryCursor(key, otherOwner, cursor)).toThrow(BffInputError);
      }
      expect(openEntryCursor(key, owner, cursor)).toBe(raw);
    },
  );

  it.each(['members', 'scopes'] as const)(
    'authenticates the literal %s purpose domain independently of the encoder',
    (selected) => {
      const key = randomBytes(32);
      const cursor = authenticated(key, Buffer.from(raw), [
        'learnstack.identity.self-context-cursor.' + selected,
        1,
        binding.id,
        binding.host,
        binding.epoch,
      ]);
      expect(openEntryCursor(key, { ...binding, purpose: selected }, cursor)).toBe(raw);
      expect(() => openEntryCursor(key, binding, cursor)).toThrow(BffInputError);
      expect(() =>
        openEntryCursor(
          key,
          { ...binding, purpose: selected === 'members' ? 'scopes' : 'members' },
          cursor,
        ),
      ).toThrow(BffInputError);
    },
  );

  it.each([
    ['another-purpose', 1],
    [purpose, 2],
  ])('rejects an otherwise authenticated wrong purpose/version %s/%s', (domain, version) => {
    const key = randomBytes(32);
    const cursor = authenticated(key, Buffer.from(raw), [
      domain,
      version,
      binding.id,
      binding.host,
      binding.epoch,
    ]);
    expect(() => openEntryCursor(key, binding, cursor)).toThrow(BffInputError);
  });

  it.each(['version', 'iv', 'ciphertext', 'tag'] as const)(
    'rejects a single changed %s byte without returning plaintext',
    (part) => {
      const key = randomBytes(32);
      const bytes = Buffer.from(sealEntryCursor(key, binding, raw), 'base64url');
      const index = { version: 0, iv: 1, ciphertext: 13, tag: bytes.length - 1 }[part];
      bytes[index] = bytes[index]! ^ 1;
      expect(() => openEntryCursor(key, binding, bytes.toString('base64url'))).toThrow(
        BffInputError,
      );
    },
  );

  it.each([8, 12] as const)(
    'refuses a valid but shortened %s-byte authentication tag',
    (length) => {
      const key = randomBytes(32);
      const cursor = authenticated(
        key,
        Buffer.from(raw),
        [purpose, 1, binding.id, binding.host, binding.epoch],
        length,
      );
      expect(() => openEntryCursor(key, binding, cursor)).toThrow(BffInputError);
    },
  );

  it('refuses truncation, appended bytes, padding and a noncanonical pad-bit alias', () => {
    const key = randomBytes(32);
    // Two bytes of plaintext produce a 31-byte envelope with unused base64 pad bits.
    const cursor = sealEntryCursor(key, binding, 'AA');
    const bytes = Buffer.from(cursor, 'base64url');
    const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_';
    const alias = cursor.slice(0, -1) + alphabet[alphabet.indexOf(cursor.at(-1)!) + 1];
    expect(Buffer.from(alias, 'base64url').equals(bytes)).toBe(true);
    for (const invalid of [
      bytes.subarray(0, -1).toString('base64url'),
      Buffer.concat([bytes, Buffer.from([0])]).toString('base64url'),
      cursor + '=',
      alias,
      '',
      'A'.repeat(1025),
    ])
      expect(() => openEntryCursor(key, binding, invalid)).toThrow(BffInputError);
    expect(openEntryCursor(key, binding, cursor)).toBe('AA');
  });

  it('refuses another or zeroed key while the intact original key still authenticates', () => {
    const key = randomBytes(32);
    const cursor = sealEntryCursor(key, binding, raw);
    expect(() => openEntryCursor(randomBytes(32), binding, cursor)).toThrow(BffInputError);
    const wiped = Buffer.from(key);
    wiped.fill(0);
    expect(() => openEntryCursor(wiped, binding, cursor)).toThrow(BffInputError);
    expect(openEntryCursor(key, binding, cursor)).toBe(raw);
  });

  it.each([0, 31, 33])('requires exactly one 32-byte key, refusing length %s', (length) => {
    const key = Buffer.alloc(length);
    expect(() => sealEntryCursor(key, binding, raw)).toThrow(BffSessionError);
    expect(() => openEntryCursor(key, binding, raw)).toThrow(BffInputError);
  });

  it('preserves the exact ASCII limit without truncation or extending the 1024-character wrapper', () => {
    const key = randomBytes(32);
    const plaintext = 'A'.repeat(739);
    const cursor = sealEntryCursor(key, binding, plaintext);
    expect(cursor).toHaveLength(1024);
    expect(openEntryCursor(key, binding, cursor)).toBe(plaintext);
    for (const value of ['', 'A'.repeat(740), 'é', 'A\0B', 'A\nB', 'A B', '\u007f']) {
      expect(() => sealEntryCursor(key, binding, value)).toThrow(BffSessionError);
      try {
        sealEntryCursor(key, binding, value);
      } catch (error) {
        expect(error).toMatchObject({ reason: 'response' });
      }
    }
  });

  it.each([Buffer.from('é'), Buffer.from([0]), Buffer.from([127]), Buffer.alloc(0)])(
    'refuses authenticated non-ASCII/control/empty plaintext at the decode boundary',
    (plaintext) => {
      const key = randomBytes(32);
      const cursor = authenticated(key, plaintext, [
        purpose,
        1,
        binding.id,
        binding.host,
        binding.epoch,
      ]);
      expect(() => openEntryCursor(key, binding, cursor)).toThrow(BffInputError);
    },
  );
});
