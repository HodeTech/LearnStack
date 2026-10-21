import { createHash } from 'node:crypto';
import { readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import { RealmError, realmFailure, refuse } from './development-realm-errors.mjs';
import { extractStockConsoleCanonicalGraph, readStockConsoleGraph } from './development-realm-stock-console.mjs';

const baselinePath = fileURLToPath(new URL('./development-realm-stock-console-baseline.json', import.meta.url));
const copy = value => structuredClone(value);
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const encode = encodeURIComponent;

function stable(value) {
  if (Array.isArray(value)) return value.map(stable);
  if (!object(value)) return value;
  return Object.fromEntries(Object.keys(value).sort().map(key => [key, stable(value[key])]));
}

/** The capture is untrusted data; absent independent endpoint reads fail closed. */
export async function extractCapturedStockConsole(capture) {
  if (!object(capture) || !Array.isArray(capture.clients) || capture.clients.length > 200) refuse('plan', 'response');
  if (Buffer.byteLength(JSON.stringify(capture.clients), 'utf8') > 256 * 1024) refuse('plan', 'body_limit');
  if (capture.clients.some(value => !object(value) || typeof value.id !== 'string' || !value.id
    || typeof value.clientId !== 'string' || !value.clientId)
    || new Set(capture.clients.map(value => value.id)).size !== capture.clients.length
    || new Set(capture.clients.map(value => value.clientId)).size !== capture.clients.length) refuse('plan', 'unsupported');
  const admin = { get: async path => {
    if (!Object.hasOwn(capture, path)) refuse('plan', 'response');
    if (Buffer.byteLength(JSON.stringify(capture[path]), 'utf8') > 256 * 1024) refuse('plan', 'body_limit');
    return copy(capture[path]);
  } };
  const client = async name => {
    const matches = capture.clients.filter(value => object(value) && value.clientId === name);
    if (matches.length !== 1 || typeof matches[0].id !== 'string' || !matches[0].id) refuse('plan', 'unsupported');
    const full = await admin.get(`clients/${encode(matches[0].id)}`);
    if (!object(full) || full.id !== matches[0].id || full.clientId !== name) refuse('plan', 'unsupported');
    return full;
  };
  const account = await client('account');
  const console = await client('account-console');
  const base = `clients/${encode(console.id)}`;
  const existingAccountGraph = { account, console,
    defaults: await admin.get(`${base}/default-client-scopes`),
    optional: await admin.get(`${base}/optional-client-scopes`),
    directMappers: await admin.get(`${base}/protocol-mappers/models`),
    allowances: new Map([[base, await admin.get(`${base}/scope-mappings`)]]),
  };
  const graph = await readStockConsoleGraph({ admin, existingAccountGraph });
  const canonical = extractStockConsoleCanonicalGraph({ graph });
  const bytes = JSON.stringify(stable(canonical)) + '\n';
  return { canonical, canonicalSha256: createHash('sha256').update(bytes).digest('hex'), bytes };
}

async function main(arguments_) {
  if (arguments_.length < 2 || arguments_.length > 3 || !['--check', '--candidate'].includes(arguments_[0])
    || (arguments_[0] === '--check' && arguments_.length !== 2)
    || (arguments_[0] === '--candidate' && arguments_.length !== 3)) refuse('preflight', 'configuration');
  const captureBytes = await readFile(arguments_[1]);
  if (captureBytes.length > 32 * 1024 * 1024) refuse('plan', 'body_limit');
  let capture;
  try { capture = JSON.parse(captureBytes.toString('utf8')); }
  catch { refuse('plan', 'response'); }
  const result = await extractCapturedStockConsole(capture);
  if (arguments_[0] === '--candidate') {
    if (resolve(arguments_[2]) === baselinePath) refuse('preflight', 'configuration');
    await writeFile(arguments_[2], result.bytes, { flag: 'wx', mode: 0o600 });
  }
  process.stdout.write(`stock-console: fixed expectation matched; sha256=${result.canonicalSha256}\n`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { await main(process.argv.slice(2)); }
  catch (error) {
    process.stderr.write(realmFailure(error instanceof RealmError ? error : new RealmError('preflight', 'configuration')) + '\n');
    process.exitCode = 1;
  }
}
