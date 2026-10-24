import { closeSync, constants, fstatSync, openSync, readSync } from 'node:fs';
import { resolve } from 'node:path';

import { parseLocalEnvironment } from '../ingress.js';
import { BffProviderError } from './provider-error.js';

export const BFF_ENV_KEYS = Object.freeze([
  'LEARNSTACK_BFF_ENABLED',
  'LEARNSTACK_BFF_ISSUER',
  'KEYCLOAK_WEB_CLIENT_SECRET',
] as const);
export type BffConfiguration = Readonly<
  | { enabled: false }
  | { enabled: true; issuer: string; clientSecret: string; allowLoopbackHttp: boolean }
>;
export type BffConfigurationIssue =
  | 'activation'
  | 'source_conflict'
  | 'private_file'
  | 'issuer'
  | 'client_secret'
  | 'source_file';

/** Closed startup context; contains no source path, setting name or private value. */
export class BffConfigurationError extends BffProviderError {
  constructor(readonly issue: BffConfigurationIssue) {
    super('configuration');
  }
}

const refused = (issue: BffConfigurationIssue): never => {
  throw new BffConfigurationError(issue);
};
const maximumBytes = 256 * 1024;
function source(path: string) {
  let descriptor: number | undefined;
  try {
    descriptor = openSync(path, constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK);
    const state = fstatSync(descriptor);
    if (!state.isFile() || state.size > maximumBytes) return refused('source_file');
    const bytes = Buffer.alloc(maximumBytes + 1);
    let size = 0;
    while (size < bytes.length) {
      const count = readSync(descriptor, bytes, size, bytes.length - size, null);
      if (count === 0) break;
      size += count;
    }
    if (size > maximumBytes) return refused('source_file');
    const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes.subarray(0, size));
    return {
      values: parseLocalEnvironment(text),
      privateFile:
        state.uid === process.getuid?.() && state.nlink === 1 && (state.mode & 0o077) === 0,
    };
  } catch (error) {
    if (
      descriptor === undefined &&
      error instanceof Error &&
      'code' in error &&
      error.code === 'ENOENT'
    )
      return { values: {}, privateFile: true };
    return refused('source_file');
  } finally {
    if (descriptor !== undefined) closeSync(descriptor);
  }
}

/** Native-only startup selection. Disabled mode never returns an issuer or secret. */
export function bffConfiguration(
  root: string,
  environment: Readonly<Record<string, string | undefined>>,
): BffConfiguration {
  try {
    const sources = [
      source(resolve(root, '.env')),
      source(resolve(root, 'frontend/apps/web/.env.local')),
    ];
    const agree = (key: (typeof BFF_ENV_KEYS)[number]) => {
      const values = [...sources.map((source) => source.values[key]), environment[key]].filter(
        (value) => value !== undefined,
      );
      if (values.some((value) => typeof value !== 'string') || new Set(values).size > 1)
        return refused('source_conflict');
      return values[0];
    };
    const enabled = agree('LEARNSTACK_BFF_ENABLED');
    if (enabled !== undefined && enabled !== 'true' && enabled !== 'false')
      return refused('activation');
    if (enabled !== 'true') return Object.freeze({ enabled: false });
    if (sources.some((source) => !source.privateFile)) return refused('private_file');
    const issuer = agree('LEARNSTACK_BFF_ISSUER');
    const clientSecret = agree('KEYCLOAK_WEB_CLIENT_SECRET');
    if (!issuer) return refused('issuer');
    if (!clientSecret || /[\r\n\0]/.test(clientSecret)) return refused('client_secret');
    let url: URL;
    try {
      url = new URL(issuer);
    } catch {
      return refused('issuer');
    }
    const allowLoopbackHttp = environment.NODE_ENV === 'development';
    if (
      url.href !== issuer ||
      url.pathname !== '/realms/learnstack' ||
      url.search ||
      url.hash ||
      url.username ||
      url.password ||
      !(
        url.protocol === 'https:' ||
        (allowLoopbackHttp &&
          url.protocol === 'http:' &&
          ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname))
      )
    )
      return refused('issuer');
    return Object.freeze({ enabled: true, issuer, clientSecret, allowLoopbackHttp });
  } catch (error) {
    if (error instanceof BffConfigurationError) throw error;
    return refused('source_file');
  }
}
