import { createHmac, timingSafeEqual } from 'node:crypto';
import { readFileSync } from 'node:fs';
import type { IncomingMessage, ServerResponse } from 'node:http';
import { isIP } from 'node:net';
import { resolve } from 'node:path';

export const INGRESS_HEADER = 'x-learnstack-ingress-provenance';
export const MAX_TARGET_BYTES = 8192;
const MAX_ENVELOPE_BYTES = 12288;
const MAC_DOMAIN = 'learnstack.public-ingress.v1\0';
export const PUBLIC_ENV_KEYS = [
  'LEARNSTACK_PUBLIC_API_ORIGIN',
  'LEARNSTACK_PUBLIC_HOP_SECRET',
  'LEARNSTACK_PUBLIC_TLS_CERT',
  'LEARNSTACK_PUBLIC_TLS_KEY',
] as const;

export interface IngressContext {
  readonly host: string;
  readonly peer: string;
  readonly method: string;
  readonly target: string;
}

export interface PublicServerConfiguration {
  readonly apiOrigin: string;
  readonly secret: string;
  readonly certificate: string;
  readonly privateKey: string;
}

function containsControl(value: string): boolean {
  return [...value].some(
    (character) => character.charCodeAt(0) < 32 || character.charCodeAt(0) === 127,
  );
}

/** Strict literals only; all IPv4-mapped forms share the direct IPv4 identity. */
export function canonicalAddress(value: string): string | null {
  if (value.length > 45 || value.includes('%')) return null;
  const family = isIP(value);
  if (family === 4) return value;
  if (family !== 6) return null;
  const canonical = new URL('http://[' + value + ']/').hostname.slice(1, -1);
  const mapped = /^::ffff:([0-9a-f]+):([0-9a-f]+)$/.exec(canonical);
  if (!mapped) return canonical;
  const high = Number.parseInt(mapped[1] ?? '', 16);
  const low = Number.parseInt(mapped[2] ?? '', 16);
  return [high >>> 8, high & 255, low >>> 8, low & 255].join('.');
}

/** Syntax only. The API, never this ingress, decides whether a host is live. */
export function normalizeHost(value: string): string | null {
  if (
    value.length === 0 ||
    value.length > 260 ||
    containsControl(value) ||
    /[\s\\/@?#,]/.test(value)
  ) {
    return null;
  }
  const match = /^(\[[0-9a-fA-F:]+\]|[^:]+)(?::([0-9]+))?$/.exec(value);
  if (!match) return null;
  const port = match[2];
  if (port && (!/^[1-9][0-9]{0,4}$/.test(port) || Number(port) > 65535)) return null;
  let name = (match[1] ?? '').toLowerCase();
  if (name.startsWith('[')) {
    const address = canonicalAddress(name.slice(1, -1));
    if (!address || isIP(address) !== 6) return null;
    name = '[' + address + ']';
  } else {
    name = name.replace(/\.$/, '');
    if (
      name.length > 253 ||
      !name.split('.').every((label) => /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(label))
    )
      return null;
  }
  return name + (port ? ':' + port : '');
}

export function validTarget(value: string): boolean {
  if (
    Buffer.byteLength(value) > MAX_TARGET_BYTES ||
    !value.startsWith('/') ||
    value.startsWith('//') ||
    containsControl(value) ||
    /[\s\\"<>#]/.test(value) ||
    [...value].some((character) => character.charCodeAt(0) > 126)
  )
    return false;
  const path = value.split('?')[0] ?? '';
  // Refuse path identities that URL normalization would turn into another route.
  if (/%(?:2f|5c|00|0a|0d)/i.test(path)) return false;
  try {
    const decoded = decodeURIComponent(path);
    return (
      !decoded.split('/').some((segment) => segment === '.' || segment === '..') &&
      !containsControl(decoded) &&
      !decoded.includes('\\')
    );
  } catch {
    return false;
  }
}

export function validSecret(value: string | undefined): value is string {
  return value !== undefined && /^[\x21-\x7e]{32,256}$/.test(value);
}

function signature(payload: string, secret: string): Buffer {
  return createHmac('sha256', secret).update(MAC_DOMAIN).update(payload).digest();
}

export function mintProvenance(context: IngressContext, secret: string): string {
  if (
    !validSecret(secret) ||
    normalizeHost(context.host) !== context.host ||
    canonicalAddress(context.peer) !== context.peer ||
    !/^[A-Z]{1,20}$/.test(context.method) ||
    !validTarget(context.target)
  )
    throw new Error('Invalid ingress context');
  const payload = Buffer.from(
    JSON.stringify([context.host, context.peer, context.method, context.target]),
  ).toString('base64url');
  return 'v1.' + payload + '.' + signature(payload, secret).toString('base64url');
}

export function verifyProvenance(
  envelope: string | null,
  secret: string | undefined,
  expected?: { readonly method: string; readonly target: string },
): IngressContext | null {
  if (!envelope || !validSecret(secret) || Buffer.byteLength(envelope) > MAX_ENVELOPE_BYTES) {
    return null;
  }
  const parts = /^v1\.([A-Za-z0-9_-]+)\.([A-Za-z0-9_-]{43})$/.exec(envelope);
  if (!parts) return null;
  const payload = parts[1] ?? '';
  const mac = Buffer.from(parts[2] ?? '', 'base64url');
  const bytes = Buffer.from(payload, 'base64url');
  if (
    bytes.toString('base64url') !== payload ||
    mac.toString('base64url') !== parts[2] ||
    !timingSafeEqual(signature(payload, secret), mac)
  )
    return null;
  try {
    const values: unknown = JSON.parse(bytes.toString('utf8'));
    if (
      !Array.isArray(values) ||
      values.length !== 4 ||
      !values.every((value): value is string => typeof value === 'string')
    )
      return null;
    const [host, peer, method, target] = values as [string, string, string, string];
    if (
      normalizeHost(host) !== host ||
      canonicalAddress(peer) !== peer ||
      !/^[A-Z]{1,20}$/.test(method) ||
      !validTarget(target) ||
      (expected && (expected.method !== method || expected.target !== target))
    )
      return null;
    return Object.freeze({ host, peer, method, target });
  } catch {
    return null;
  }
}

function privateInboundHeader(name: string): boolean {
  return /^(?:forwarded|x-forwarded-.*|x-learnstack-.*|x-tenant-id|x-organization-id|x-locale|x-middleware-.*|x-invoke-.*|x-nextjs-.*)$/i.test(
    name,
  );
}

/** Called before Next, including upgrades. Never signs a forwarded peer. */
export function admitIncomingRequest(request: IncomingMessage, secret: string): boolean {
  const hosts: string[] = [];
  for (let i = 0; i < request.rawHeaders.length; i += 2) {
    if (request.rawHeaders[i]?.toLowerCase() === 'host')
      hosts.push(request.rawHeaders[i + 1] ?? '');
  }
  const host = hosts.length === 1 ? normalizeHost(hosts[0] ?? '') : null;
  const peer = canonicalAddress(request.socket.remoteAddress ?? '');
  const method = request.method ?? '';
  const target = request.url ?? '';
  if (!host || !peer || !/^[A-Z]{1,20}$/.test(method) || !validTarget(target)) return false;
  for (const name of Object.keys(request.headers)) {
    if (privateInboundHeader(name)) delete request.headers[name];
  }
  const safeRaw: string[] = [];
  for (let i = 0; i < request.rawHeaders.length; i += 2) {
    const name = request.rawHeaders[i] ?? '';
    if (!privateInboundHeader(name)) {
      safeRaw.push(name, name.toLowerCase() === 'host' ? host : (request.rawHeaders[i + 1] ?? ''));
    }
  }
  const envelope = mintProvenance({ host, peer, method, target }, secret);
  request.headers.host = host;
  request.headers[INGRESS_HEADER] = envelope;
  request.rawHeaders = [...safeRaw, INGRESS_HEADER, envelope];
  return true;
}

export function refuseIngress(response: ServerResponse): void {
  response.writeHead(404, { 'cache-control': 'no-store', 'content-type': 'text/plain' });
  response.end('Not found');
}

/** No eval, interpolation or shell parsing; malformed/duplicate keys fail closed. */
export function parseLocalEnvironment(source: string): Record<string, string> {
  const values: Record<string, string> = Object.create(null) as Record<string, string>;
  for (const line of source.split(/\r?\n/)) {
    if (/^\s*(?:#.*)?$/.test(line)) continue;
    const match = /^([A-Za-z_][A-Za-z0-9_]*)=(.*)$/.exec(line);
    if (!match || Object.hasOwn(values, match[1] ?? ''))
      throw new Error('Invalid local environment');
    const key = match[1] ?? '';
    let value = match[2] ?? '';
    if (value.startsWith("'") || value.startsWith('"')) {
      if (value.length < 2 || value.at(-1) !== value[0])
        throw new Error('Invalid local environment');
      value = value.slice(1, -1);
    }
    values[key] = value;
  }
  return values;
}

function readOptionalEnvironment(path: string): Record<string, string> {
  try {
    return parseLocalEnvironment(readFileSync(path, 'utf8'));
  } catch (error) {
    if (error instanceof Error && 'code' in error && error.code === 'ENOENT') return {};
    throw new Error('Cannot read local environment');
  }
}

export function loadLocalEnvironment(
  root: string,
  environment: Readonly<Record<string, string | undefined>>,
): Record<string, string> {
  const source = readOptionalEnvironment(resolve(root, '.env'));
  const projection = readOptionalEnvironment(resolve(root, 'frontend/apps/web/.env.local'));
  const result = { ...source };
  for (const key of PUBLIC_ENV_KEYS) {
    const values = [source[key], projection[key], environment[key]].filter(
      (value): value is string => value !== undefined,
    );
    if (new Set(values).size > 1) throw new Error('Conflicting public server configuration');
    if (values[0] !== undefined) result[key] = values[0];
  }
  for (const key of ['ConnectionStrings__Default', 'ConnectionStrings__PlatformAdmin']) {
    if (environment[key] !== undefined) result[key] = environment[key];
  }
  return result;
}

export function publicServerConfiguration(
  values: Record<string, string | undefined>,
  root: string,
): PublicServerConfiguration {
  const secret = values.LEARNSTACK_PUBLIC_HOP_SECRET;
  const origin = values.LEARNSTACK_PUBLIC_API_ORIGIN;
  const certificate = values.LEARNSTACK_PUBLIC_TLS_CERT;
  const privateKey = values.LEARNSTACK_PUBLIC_TLS_KEY;
  if (!validSecret(secret) || !origin || !certificate || !privateKey) {
    throw new Error('Incomplete public server configuration');
  }
  try {
    const url = new URL(origin);
    if (
      url.protocol !== 'http:' ||
      url.hostname !== '127.0.0.1' ||
      url.username ||
      url.password ||
      url.pathname !== '/' ||
      url.search ||
      url.hash ||
      origin !== url.origin
    )
      throw new Error();
  } catch {
    throw new Error('Invalid private API origin');
  }
  return Object.freeze({
    apiOrigin: origin,
    secret,
    certificate: resolve(root, certificate),
    privateKey: resolve(root, privateKey),
  });
}
