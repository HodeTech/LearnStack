import { createCipheriv, createDecipheriv, randomBytes } from 'node:crypto';

import { BffInputError } from './input-error.js';
import { BffSessionError } from './session-contract.js';

type EntryCursorBinding = Readonly<{ id: string; host: string; epoch: string }>;
const VERSION = 1;
const MAX_PLAINTEXT_BYTES = 739;
const aad = (binding: EntryCursorBinding) =>
  Buffer.from(
    JSON.stringify([
      'learnstack.identity.self-context-cursor',
      VERSION,
      binding.id,
      binding.host,
      binding.epoch,
    ]),
    'utf8',
  );

/** Native-only envelope; a browser cursor carries no readable API identity tuple. */
export function sealEntryCursor(key: Buffer, binding: EntryCursorBinding, cursor: string): string {
  if (
    !Buffer.isBuffer(key) ||
    key.length !== 32 ||
    typeof cursor !== 'string' ||
    cursor.length < 1 ||
    cursor.length > MAX_PLAINTEXT_BYTES ||
    !/^[\x21-\x7e]+$/.test(cursor)
  )
    throw new BffSessionError('response');
  const iv = randomBytes(12);
  const cipher = createCipheriv('aes-256-gcm', key, iv, { authTagLength: 16 });
  cipher.setAAD(aad(binding));
  return Buffer.concat([
    Buffer.from([VERSION]),
    iv,
    cipher.update(cursor, 'ascii'),
    cipher.final(),
    cipher.getAuthTag(),
  ]).toString('base64url');
}

/** Invalid/foreign wrappers are input failures before any API or transition work. */
export function openEntryCursor(key: Buffer, binding: EntryCursorBinding, cursor: string): string {
  try {
    if (
      !Buffer.isBuffer(key) ||
      key.length !== 32 ||
      typeof cursor !== 'string' ||
      cursor.length > 1024 ||
      !/^[A-Za-z0-9_-]+$/.test(cursor)
    )
      throw new BffInputError();
    const envelope = Buffer.from(cursor, 'base64url');
    if (
      envelope.length < 30 ||
      envelope.length > 768 ||
      envelope[0] !== VERSION ||
      envelope.toString('base64url') !== cursor
    )
      throw new BffInputError();
    const decipher = createDecipheriv('aes-256-gcm', key, envelope.subarray(1, 13), {
      authTagLength: 16,
    });
    decipher.setAAD(aad(binding));
    decipher.setAuthTag(envelope.subarray(-16));
    const plaintext = Buffer.concat([
      decipher.update(envelope.subarray(13, -16)),
      decipher.final(),
    ]);
    if (
      plaintext.length < 1 ||
      plaintext.length > MAX_PLAINTEXT_BYTES ||
      !plaintext.every((value) => value >= 33 && value <= 126)
    )
      throw new BffInputError();
    return plaintext.toString('ascii');
  } catch {
    throw new BffInputError();
  }
}
