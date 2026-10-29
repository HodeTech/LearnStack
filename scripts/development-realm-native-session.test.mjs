import test from 'node:test';
import assert from 'node:assert/strict';
import { buildRealmPlan, nativeWebScopeDetachmentPath } from './development-realm-plan.mjs';
import { reconcileRealm } from './development-realm-core.mjs';
import { desired, manifest, secret, provider, providerTransport } from './development-realm-test-fixture.mjs';

const clone = value => structuredClone(value);
const plan = fixture => buildRealmPlan({ admin: fixture.admin, desired: clone(desired), manifest: clone(manifest), webSecret: secret });
const web = fixture => fixture.state.clients.find(client => client.clientId === 'learnstack-web');
const legacy = fixture => fixture.state.scopes.find(scope => scope.name === 'learnstack-tenant-api');
const native = fixture => fixture.state.scopes.find(scope => scope.name === 'learnstack-native-session');
function oldWeb(fixture, missingScope = false) {
  web(fixture).defaultClientScopes = web(fixture).defaultClientScopes.map(name =>
    name === 'learnstack-native-session' ? 'learnstack-tenant-api' : name);
  if (missingScope) fixture.state.scopes = fixture.state.scopes.filter(scope => scope !== native(fixture));
}
async function apply(change) {
  for (const action of change.actions) await action.apply();
  await change.verify();
}

test('native reconciliation replaces only the legacy web default, preserves API scope and repeats without writes', async () => {
  for (const missingScope of [false, true]) {
    const fixture = provider();
    oldWeb(fixture, missingScope);
    const apiBefore = clone(fixture.state.clients.find(client => client.clientId === 'learnstack-api'));
    const scopeBefore = clone(legacy(fixture));
    const usersBefore = clone(fixture.state.users);
    const change = await plan(fixture);
    assert.deepEqual(change.actions.map(action => action.label), [
      ...(missingScope ? ['create_scope'] : []), 'detach_web_legacy_scope', 'attach_default_scope',
    ]);
    await apply(change);
    assert.deepEqual(fixture.state.clients.find(client => client.clientId === 'learnstack-api'), apiBefore);
    assert.deepEqual(legacy(fixture), scopeBefore);
    assert.deepEqual(fixture.state.users, usersBefore);
    assert.ok(web(fixture).defaultClientScopes.includes('learnstack-native-session'));
    assert.ok(![...web(fixture).defaultClientScopes, ...web(fixture).optionalClientScopes].includes('learnstack-tenant-api'));
    assert.deepEqual(native(fixture).protocolMappers.map(mapper => mapper.name).sort(), ['learnstack-api-audience', 'user_id']);
    const prior = fixture.writes.length;
    assert.equal((await plan(fixture)).actions.length, 0);
    assert.equal(fixture.writes.length, prior);
  }
});

test('actual core emits one qualified legacy web DELETE and no API mutation', async () => {
  const fixture = provider();
  oldWeb(fixture, true);
  const wire = providerTransport(fixture);
  const input = { baseUrl: 'http://localhost:8080/', expectedIssuer: 'http://localhost:8080/realms/learnstack',
    adminUsername: 'admin', adminPassword: 'private-admin', webSecret: secret,
    desired: clone(desired), manifest: clone(manifest), transport: wire.transport };
  assert.equal((await reconcileRealm(input)).mutations, 3);
  const deletions = wire.requests.filter(request => request.method === 'DELETE');
  assert.deepEqual(deletions.map(request => request.path), [
    `/admin/realms/learnstack/clients/${web(fixture).id}/default-client-scopes/${legacy(fixture).id}`,
  ]);
  assert.equal((await reconcileRealm(input)).mutations, 0);
  for (const forged of [{}, { clientId: web(fixture).id, scopeId: legacy(fixture).id }, null])
    assert.throws(() => nativeWebScopeDetachmentPath(forged), error => error.reason === 'unsupported');
});

test('post-PUT callback reordering preserves the exact unique registrations and rejects dirty readback', async () => {
  for (const defect of [null, 'missing', 'extra', 'duplicate', 'foreign', 'other-client-field']) {
    const fixture = provider();
    const client = web(fixture);
    client.redirectUris = ['http://localhost:3000/legacy-callback'];
    client.description = 'retained client description';
    const expected = desired.clients.find(value => value.clientId === 'learnstack-web').redirectUris;
    const put = fixture.admin.put;
    fixture.admin.put = async (path, body) => {
      const status = await put(path, body);
      if (path === `clients/${client.id}` && Array.isArray(body.redirectUris)) {
        // Model the real provider's set-valued storage after an actual update,
        // not only an already-converged read with a different array order.
        client.redirectUris.reverse();
        if (defect === 'missing') client.redirectUris.pop();
        if (defect === 'extra') client.redirectUris.push('https://extra.fixture.invalid/callback');
        if (defect === 'duplicate') client.redirectUris.push(client.redirectUris[0]);
        if (defect === 'foreign') client.redirectUris[0] = 'https://foreign.fixture.invalid/callback';
        if (defect === 'other-client-field') client.description = 'unexpected client change';
      }
      return status;
    };
    const change = await plan(fixture);
    assert.deepEqual(change.actions.map(action => action.label), ['update_client']);
    for (const action of change.actions) await action.apply();
    assert.equal(fixture.writes.length, 1);
    if (defect === null) {
      assert.notDeepEqual(client.redirectUris, expected);
      assert.equal(client.redirectUris.length, expected.length);
      assert.equal(new Set(client.redirectUris).size, expected.length);
      assert.deepEqual(new Set(client.redirectUris), new Set(expected));
      await change.verify();
      const before = clone(fixture.state);
      const repeated = await plan(fixture);
      assert.deepEqual(repeated.actions, []);
      await apply(repeated);
      assert.equal(fixture.writes.length, 1, 'the reordered warm representation requires no repair write');
      assert.deepEqual(fixture.state, before);
    } else {
      if (defect === 'duplicate') assert.deepEqual(new Set(client.redirectUris), new Set(expected));
      await assert.rejects(change.verify(), error => error.stage === 'readback' && error.reason === 'readback_conflict');
    }
  }
});

test('native web direct/default/optional foreign context emitters refuse before any pending write', async () => {
  for (const location of ['direct', 'default', 'optional']) for (const claim of ['tenant_id', 'organization_id']) {
    const fixture = provider();
    oldWeb(fixture);
    fixture.state.realm.accessTokenLifespan = 1800;
    const mapper = { ...clone(legacy(fixture).protocolMappers.find(mapper => mapper.name === claim)), id: 'foreign-mapper' };
    if (location === 'direct') web(fixture).protocolMappers.push(mapper);
    else {
      fixture.state.scopes.push({ id: 'foreign-scope', name: 'foreign-scope', protocol: 'openid-connect', protocolMappers: [mapper] });
      web(fixture)[`${location}ClientScopes`].push('foreign-scope');
    }
    const before = clone(fixture.state);
    await assert.rejects(plan(fixture), error => error.reason === 'mapper_conflict');
    assert.deepEqual(fixture.writes, []);
    assert.deepEqual(fixture.state, before);
  }
});

test('requestable legacy scope and conflicting reserved native scope refuse instead of being repaired', async () => {
  for (const defect of ['optional-legacy', 'optional-native', 'actor', 'context', 'audience', 'attribute']) {
    const fixture = provider();
    oldWeb(fixture);
    fixture.state.realm.accessTokenLifespan = 1800;
    if (defect === 'optional-legacy') web(fixture).optionalClientScopes.push('learnstack-tenant-api');
    if (defect === 'optional-native') web(fixture).optionalClientScopes.push('learnstack-native-session');
    if (defect === 'actor') native(fixture).protocolMappers[0].config['user.attribute'] = 'other';
    if (defect === 'context') native(fixture).protocolMappers.push({ ...clone(legacy(fixture).protocolMappers[1]), id: 'conflicting-context' });
    if (defect === 'audience') native(fixture).protocolMappers[1].config['included.client.audience'] = 'other';
    if (defect === 'attribute') native(fixture).attributes['include.in.token.scope'] = 'false';
    const before = clone(fixture.state);
    await assert.rejects(plan(fixture), error => error.reason === 'mapper_conflict');
    assert.deepEqual(fixture.writes, []);
    assert.deepEqual(fixture.state, before);
  }
});

test('native assignment readback rejects omitted detachment, replaced IDs and API attachment damage', async () => {
  for (const defect of ['detachment', 'scope-id', 'api-assignment']) {
    const fixture = provider();
    oldWeb(fixture);
    const change = await plan(fixture);
    for (const action of change.actions)
      if (defect !== 'detachment' || action.label !== 'detach_web_legacy_scope') await action.apply();
    if (defect === 'scope-id') native(fixture).id = 'replacement-id';
    if (defect === 'api-assignment') fixture.state.clients.find(client => client.clientId === 'learnstack-api').defaultClientScopes = [];
    await assert.rejects(change.verify(), error => error.reason === 'readback_conflict');
  }
});
