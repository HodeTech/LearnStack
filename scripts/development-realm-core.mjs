import { buildRealmPlan } from './development-realm-plan.mjs';
import { stockConsoleDetachmentPath } from './development-realm-stock-console.mjs';
import { realmJson, realmRequest } from './development-realm-http.mjs';
import { RealmError, refuse } from './development-realm-errors.mjs';

export const realmPassMilliseconds = 120000;

/** Internal core for the owned launcher and isolated provider tests; no CLI overrides. */
export async function reconcileRealm({ baseUrl, expectedIssuer, adminUsername, adminPassword,
  webSecret, manifest, desired, signal, ca, transport = realmRequest }) {
  let base;
  try { base = new URL(baseUrl); }
  catch { refuse('preflight', 'configuration'); }
  if (!['localhost', '127.0.0.1', '[::1]'].includes(base.hostname)
      || base.pathname !== '/' || base.search || base.hash || base.username || base.password
      || !['http:', 'https:'].includes(base.protocol)
      || expectedIssuer !== new URL('realms/learnstack', base).href
      || !adminUsername || !adminPassword || !webSecret)
    refuse('preflight', 'configuration');
  const deadline = AbortSignal.timeout(realmPassMilliseconds);
  const active = signal ? AbortSignal.any([signal, deadline]) : deadline;
  let mutations = 0;
  let stage = 'discovery';
  const request = async (path, options = {}) => {
    if (active.aborted) refuse(options.stage, signal?.aborted ? 'cancelled' : 'deadline');
    let result;
    try { result = await transport(new URL(path, base), { ...options, signal: active, ca }); }
    catch (error) {
      if (active.aborted) refuse(options.stage, signal?.aborted ? 'cancelled' : 'deadline');
      throw error;
    }
    if (active.aborted) refuse(options.stage, signal?.aborted ? 'cancelled' : 'deadline');
    return result;
  };
  try {
    const discovery = await request('realms/learnstack/.well-known/openid-configuration', { stage: 'discovery' });
    if (discovery.status !== 200 || document(discovery, 'discovery').issuer !== expectedIssuer)
      refuse('discovery', 'configuration');
    stage = 'authentication';
    let token;
    let refreshToken;
    let expiresAt = 0;
    let refreshes = 0;
    const authenticate = async form => {
      // Reserve the complete exchange budget before the token expires. Refresh
      // is proactive and bounded; no failed Admin request is ever replayed.
      const started = performance.now();
      const authentication = await request('realms/master/protocol/openid-connect/token', {
        method: 'POST', stage: 'authentication', form: { client_id: 'admin-cli', ...form },
      });
      if (authentication.status !== 200) refuse('authentication', 'status');
      const value = document(authentication, 'authentication');
      if (typeof value.access_token !== 'string' || !value.access_token || /[\r\n]/.test(value.access_token)
        || !Number.isSafeInteger(value.expires_in) || value.expires_in <= 5
        || typeof value.refresh_token !== 'string' || !value.refresh_token || /[\r\n]/.test(value.refresh_token))
        refuse('authentication', 'response');
      token = value.access_token;
      refreshToken = value.refresh_token;
      expiresAt = started + value.expires_in * 1000 - 5000;
    };
    await authenticate({ grant_type: 'password', username: adminUsername, password: adminPassword });
    const authenticatedRequest = async (path, options) => {
      if (performance.now() >= expiresAt) {
        if (refreshes >= 2) refuse('authentication', 'deadline');
        refreshes++;
        await authenticate({ grant_type: 'refresh_token', refresh_token: refreshToken });
      }
      return request(path, { ...options, bearer: token });
    };
    stage = 'version';
    const version = await authenticatedRequest('admin/serverinfo', { stage: 'version' });
    if (version.status !== 200 || document(version, 'version').systemInfo?.version !== '26.6.2')
      refuse('version', 'unsupported');
    stage = 'plan';
    const admin = {
      get: async (path) => {
        const result = await authenticatedRequest(adminPath(path), { stage });
        if (result.status !== 200) refuse(stage, 'status');
        return realmJson(result, stage);
      },
      put: async (path, body) => {
        const result = await authenticatedRequest(adminPath(path), { method: 'PUT', json: body, stage });
        mutations++;
        if (result.status < 200 || result.status >= 300) refuse(stage, 'status');
        return result.status;
      },
      // This is deliberately not a generic DELETE API. Only an opaque binding
      // minted by the complete stock-graph qualifier can name one assignment.
      detachStockConsoleAssignment: async binding => {
        const path = stockConsoleDetachmentPath(binding, stage);
        mutations++;
        const result = await authenticatedRequest(adminPath(path), { method: 'DELETE', stage });
        if (result.status !== 204) refuse(stage, 'status');
      },
      post: async (path, body) => {
        const result = await authenticatedRequest(adminPath(path), { method: 'POST', json: body, stage });
        mutations++;
        if ((result.status < 200 || result.status >= 300) && result.status !== 409) refuse(stage, 'status');
        return { status: result.status, ...(path === 'partialImport' && result.status === 200 ? { body: document(result, stage) } : {}) };
      },
    };
    const plan = await buildRealmPlan({ admin, desired, manifest, webSecret });
    stage = 'apply';
    for (const action of plan.actions) {
      if (active.aborted) refuse(stage, signal?.aborted ? 'cancelled' : 'deadline');
      await action.apply();
    }
    stage = 'readback';
    await plan.verify();
    const remaining = await buildRealmPlan({ admin, desired, manifest, webSecret });
    if (remaining.actions.length !== 0) refuse(stage, 'readback_conflict');
    return Object.freeze({ mutations });
  } catch (error) {
    if (error instanceof RealmError) throw error;
    refuse(stage, 'response');
  }
}

function document(result, stage) {
  const value = realmJson(result, stage);
  if (!value || typeof value !== 'object' || Array.isArray(value)) refuse(stage, 'response');
  return value;
}

function adminPath(path) {
  if (typeof path !== 'string' || path.startsWith('/') || /[\r\n#\\]/.test(path)
      || path.split(/[/?]/).includes('..')) refuse('plan', 'configuration');
  return `admin/realms/learnstack/${path}`;
}
