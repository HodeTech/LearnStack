import { buildRealmPlan } from './development-realm-plan.mjs';
import { realmJson, realmRequest } from './development-realm-http.mjs';
import { RealmError, refuse } from './development-realm-errors.mjs';

export const realmPassMilliseconds = 120000;

/** Internal core for the owned launcher and isolated provider tests; no CLI overrides. */
export async function reconcileRealm({ baseUrl, expectedIssuer, adminUsername, adminPassword,
  webSecret, manifest, desired, signal, ca, transport = realmRequest }) {
  const base = new URL(baseUrl);
  if (!['localhost', '127.0.0.1', '[::1]'].includes(base.hostname)
      || base.pathname !== '/' || base.search || base.hash || base.username || base.password
      || !['http:', 'https:'].includes(base.protocol)
      || expectedIssuer !== new URL('realms/learnstack', base).href
      || !adminUsername || !adminPassword || !webSecret)
    refuse('preflight', 'configuration');
  const deadline = AbortSignal.timeout(realmPassMilliseconds);
  const active = signal ? AbortSignal.any([signal, deadline]) : deadline;
  let mutations = 0;
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
    if (discovery.status !== 200 || realmJson(discovery, 'discovery').issuer !== expectedIssuer)
      refuse('discovery', 'configuration');
    const authentication = await request('realms/master/protocol/openid-connect/token', {
      method: 'POST', stage: 'authentication', form: { grant_type: 'password', client_id: 'admin-cli',
        username: adminUsername, password: adminPassword },
    });
    if (authentication.status !== 200) refuse('authentication', 'status');
    const token = realmJson(authentication, 'authentication').access_token;
    if (typeof token !== 'string' || !token || /[\r\n]/.test(token)) refuse('authentication', 'response');
    const version = await request('admin/serverinfo', { bearer: token, stage: 'version' });
    if (version.status !== 200 || realmJson(version, 'version').systemInfo?.version !== '26.6.2')
      refuse('version', 'unsupported');
    let stage = 'plan';
    const admin = {
      get: async (path) => {
        const result = await request(adminPath(path), { bearer: token, stage });
        if (result.status !== 200) refuse(stage, 'status');
        return realmJson(result, stage);
      },
      put: async (path, body) => {
        const result = await request(adminPath(path), { method: 'PUT', json: body, bearer: token, stage });
        mutations++;
        if (result.status < 200 || result.status >= 300) refuse(stage, 'status');
        return result.status;
      },
      post: async (path, body) => {
        const result = await request(adminPath(path), { method: 'POST', json: body, bearer: token, stage });
        mutations++;
        if ((result.status < 200 || result.status >= 300) && result.status !== 409) refuse(stage, 'status');
        return { status: result.status };
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
    refuse('plan', 'response');
  }
}

function adminPath(path) {
  if (typeof path !== 'string' || path.startsWith('/') || /[\r\n#\\]/.test(path)
      || path.split(/[/?]/).includes('..')) refuse('plan', 'configuration');
  return `admin/realms/learnstack/${path}`;
}
