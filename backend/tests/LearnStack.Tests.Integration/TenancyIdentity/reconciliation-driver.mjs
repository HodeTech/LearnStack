// Test-only transport controls; the production launcher accepts no fixture endpoint or scenario.
import { reconcileRealm } from '../../../../scripts/development-realm-core.mjs';
import { realmRequest } from '../../../../scripts/development-realm-http.mjs';
import { RealmError } from '../../../../scripts/development-realm-errors.mjs';
let writes = 0;
let input;
try {
  if (Number(process.versions.node.split('.')[0]) < 22) throw new RealmError('preflight', 'unsupported');
  const chunks = [];
  let size = 0;
  for await (const chunk of process.stdin) {
    size += chunk.length;
    if (size > 256 * 1024) throw new RealmError('preflight', 'body_limit');
    chunks.push(chunk);
  }
  input = JSON.parse(Buffer.concat(chunks).toString('utf8'));
  const scenarios = new Set(['normal', 'lost_acknowledgement', 'partial_cancel', 'readback_failure', 'duplicate_lookup']);
  if (!scenarios.has(input.scenario)) throw new RealmError('preflight', 'configuration');
  const abort = new AbortController();
  let injected = false;
  const transport = async (url, options) => {
    const mutation = url.pathname.startsWith('/admin/realms/learnstack/')
      && options.method !== undefined && options.method !== 'GET';
    if (mutation) writes++;
    const response = await realmRequest(url, options);
    if (mutation) {
      if (!injected && response.status >= 200 && response.status < 300) {
        if (input.scenario === 'lost_acknowledgement') {
          injected = true;
          // Acknowledgement loss is injected only after the real provider committed and closed its response.
          throw new RealmError(options.stage, 'transport');
        }
        if (input.scenario === 'partial_cancel') {
          injected = true;
          abort.abort();
        }
      }
    }
    if (!injected && input.scenario === 'readback_failure' && options.stage === 'readback') {
      injected = true;
      throw new RealmError('readback', 'transport');
    }
    if (!injected && input.scenario === 'duplicate_lookup'
        && options.stage === 'plan' && /\/users$/.test(url.pathname)) {
      const users = JSON.parse(response.bytes.toString('utf8'));
      if (Array.isArray(users) && users.length > 0) {
        injected = true;
        return { ...response, bytes: Buffer.from(JSON.stringify([...users, users[0]])) };
      }
    }
    return response;
  };
  const result = await reconcileRealm({ ...input, signal: abort.signal, transport });
  if (writes !== result.mutations || (input.scenario !== 'normal' && !injected))
    throw new RealmError('readback', 'response', result.preparationDispatched === true);
  process.stdout.write(JSON.stringify({ outcome: 'success', mutations: writes,
    ...(result.preparationDispatched === true ? { preparationDispatched: true } : {}) }) + '\n');
} catch (error) {
  const closed = error instanceof RealmError ? error : new RealmError('preflight', 'configuration');
  process.stdout.write(JSON.stringify({ outcome: 'failure', mutations: writes,
    stage: closed.stage, reason: closed.reason,
    ...(closed.preparationDispatched === true ? { preparationDispatched: true } : {}) }) + '\n');
  process.exitCode = 1;
} finally {
  input = undefined;
}
