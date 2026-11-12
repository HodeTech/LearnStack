import { closeSync, constants, fstatSync, openSync, readSync } from 'node:fs';
import { resolve } from 'node:path';

import { parseLocalEnvironment } from '../ingress.js';
import { BffConfigurationError } from './configuration.js';

const keys = ['ENABLED', 'URL', 'BIND_ADDRESS', 'TLS_CERT', 'TLS_KEY', 'CA', 'NATIVE_CA'].map(
  (name) => `LEARNSTACK_BFF_BACKCHANNEL_${name}`,
);
const fail = (): never => {
  throw new BffConfigurationError('source_file');
};
function source(path: string) {
  let fd: number | undefined;
  try {
    fd = openSync(path, constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK);
    const stat = fstatSync(fd);
    if (!stat.isFile() || stat.size > 256 * 1024) return fail();
    const bytes = Buffer.alloc(256 * 1024 + 1);
    let size = 0;
    while (size < bytes.length) {
      const count = readSync(fd, bytes, size, bytes.length - size, null);
      if (!count) break;
      size += count;
    }
    if (size > 256 * 1024) return fail();
    return {
      values: parseLocalEnvironment(
        new TextDecoder('utf-8', { fatal: true }).decode(bytes.subarray(0, size)),
      ),
      privateFile: stat.uid === process.getuid?.() && stat.nlink === 1 && (stat.mode & 0o077) === 0,
    };
  } catch (error) {
    if (fd === undefined && (error as NodeJS.ErrnoException).code === 'ENOENT')
      return { values: {}, privateFile: true };
    return fail();
  } finally {
    if (fd !== undefined) closeSync(fd);
  }
}
export type BackchannelConfiguration = Readonly<
  | { enabled: false }
  | {
      enabled: true;
      authority: string;
      port: number;
      bindAddress: '127.0.0.1' | '0.0.0.0';
      certificate: string;
      privateKey: string;
      ca: string;
      nativeCa: string;
    }
>;
export function backchannelConfiguration(
  root: string,
  environment: Readonly<Record<string, string | undefined>>,
  bffEnabled: boolean,
): BackchannelConfiguration {
  const sources = [
    source(resolve(root, '.env')),
    source(resolve(root, 'frontend/apps/web/.env.local')),
  ];
  const agree = (index: number) => {
    const values = [
      ...sources.map((item) => item.values[keys[index]!]),
      environment[keys[index]!],
    ].filter((value): value is string => value !== undefined);
    if (new Set(values).size > 1) return fail();
    return values[0];
  };
  const enabled = agree(0);
  if (enabled !== undefined && enabled !== 'true' && enabled !== 'false') return fail();
  if (enabled !== 'true') return Object.freeze({ enabled: false });
  if (!bffEnabled || sources.some((item) => !item.privateFile)) return fail();
  const raw = agree(1);
  let url: URL;
  try {
    url = new URL(raw!);
  } catch {
    return fail();
  }
  if (
    url.protocol !== 'https:' ||
    url.href !== raw ||
    url.pathname !== '/api/auth/backchannel/keycloak' ||
    url.search ||
    url.hash ||
    url.username ||
    url.password
  )
    return fail();
  const port = Number(url.port || '443');
  const bindAddress = agree(2);
  const certificate = agree(3),
    privateKey = agree(4),
    ca = agree(5),
    nativeCa = agree(6);
  if (
    !Number.isInteger(port) ||
    port < 1 ||
    port > 65535 ||
    (bindAddress !== '127.0.0.1' && bindAddress !== '0.0.0.0') ||
    !certificate ||
    !privateKey ||
    !ca ||
    !nativeCa ||
    [certificate, privateKey, ca, nativeCa].some((value) => /[\0\r\n]/u.test(value))
  )
    return fail();
  return Object.freeze({
    enabled: true,
    authority: url.host,
    port,
    bindAddress,
    certificate: resolve(root, certificate),
    privateKey: resolve(root, privateKey),
    ca: resolve(root, ca),
    nativeCa: resolve(root, nativeCa),
  });
}
