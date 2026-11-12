import test from 'node:test';
import assert from 'node:assert/strict';
import { buildRealmPlan, buildRealmPreparation } from './development-realm-plan.mjs';
import { reconcileRealm } from './development-realm-core.mjs';
import { desired, manifest, secret, provider, providerTransport } from './development-realm-test-fixture.mjs';

const backchannel = Object.freeze({ enabled: true, url: 'https://host.docker.internal:3443/api/auth/backchannel/keycloak' });
const clone = structuredClone;
const web = fixture => fixture.state.clients.find(value => value.clientId === 'learnstack-web');
const plan = (fixture, configuration = backchannel) => buildRealmPlan({ admin: fixture.admin,
  desired: clone(desired), manifest: clone(manifest), webSecret: secret, backchannel: configuration });
const apply = async change => { for (const action of change.actions) await action.apply(); await change.verify(); };

test('fresh and warm provider setup owns only exact web callback fields, separately reads back and reruns without writes', async () => {
  for (const warm of [false, true]) {
    const fixture = provider();
    const client = web(fixture);
    const retained = clone(fixture.state);
    if (warm) {
      client.attributes['backchannel.logout.url'] = backchannel.url;
      client.attributes['backchannel.logout.session.required'] = 'false';
      client.attributes['backchannel.logout.revoke.offline.tokens'] = 'true';
      client.frontchannelLogout = true;
    }
    const change = await plan(fixture);
    assert.deepEqual(change.actions.map(value => value.label), ['update_client']);
    const readCount = fixture.reads.length;
    await apply(change);
    assert.ok(fixture.reads.length > readCount, 'verification performs an independent fresh snapshot');
    assert.equal(fixture.writes.length, 1);
    const patch = fixture.writes[0];
    assert.equal(patch.path, `clients/${client.id}`);
    assert.deepEqual(patch.body, { frontchannelLogout: false, attributes: {
      ...(warm ? {} : { 'backchannel.logout.url': backchannel.url }), 'backchannel.logout.session.required': 'true',
      'backchannel.logout.revoke.offline.tokens': 'false',
    }, webOrigins: [], authorizationServicesEnabled: false });
    assert.equal(client.frontchannelLogout, false);
    const { attributes, frontchannelLogout, authorizationServicesEnabled, ...actual } = client;
    assert.equal(authorizationServicesEnabled, false);
    const { attributes: oldAttributes, ...old } = retained.clients.find(value => value.clientId === 'learnstack-web');
    assert.deepEqual(actual, old);
    assert.deepEqual(Object.fromEntries(Object.entries(attributes).filter(([key]) => !key.startsWith('backchannel.'))), oldAttributes);
    assert.deepEqual(fixture.state.clients.filter(value => value !== client), retained.clients.filter(value => value.clientId !== client.clientId));
    assert.deepEqual(fixture.state.users, retained.users);
    assert.ok(client.optionalClientScopes.includes('offline_access'));
    const after = clone(fixture.state);
    const repeated = await plan(fixture);
    assert.deepEqual(repeated.actions, []);
    await apply(repeated);
    assert.equal(fixture.writes.length, 1);
    assert.deepEqual(fixture.state, after);
  }
});

test('shared core activation and disabled configuration preserve separate exact plans', async () => {
  const fixture = provider();
  const wire = providerTransport(fixture);
  const input = { baseUrl: 'http://localhost:8080/', expectedIssuer: 'http://localhost:8080/realms/learnstack',
    adminUsername: 'admin', adminPassword: 'private-admin', webSecret: secret,
    desired: clone(desired), manifest: clone(manifest), transport: wire.transport, backchannel };
  assert.equal((await reconcileRealm(input)).mutations, 1);
  assert.equal((await reconcileRealm(input)).mutations, 0);
  web(fixture).attributes['backchannel.logout.url'] = 'https://foreign.invalid/logout';
  web(fixture).frontchannelLogout = true;
  const before = clone(fixture.state);
  await apply(await plan(fixture, { enabled: false }));
  assert.deepEqual(fixture.state, before);
});

test('foreign callback, default offline scope and unsupported signing/encryption refuse all pending writes', async () => {
  for (const defect of ['foreign', 'offline', 'algorithm', 'realm-algorithm', 'encrypted-algorithm', 'encrypted-encoding']) {
    const fixture = provider();
    const client = web(fixture);
    fixture.state.realm.accessTokenLifespan = 1800;
    if (defect === 'foreign') client.attributes['backchannel.logout.url'] = 'https://foreign.invalid/logout';
    if (defect === 'offline') client.defaultClientScopes.push('offline_access');
    if (defect === 'algorithm') client.attributes['id.token.signed.response.alg'] = 'PS256';
    if (defect === 'realm-algorithm') fixture.state.realm.defaultSignatureAlgorithm = 'HS256';
    if (defect === 'encrypted-algorithm') client.attributes['id.token.encrypted.response.alg'] = 'RSA-OAEP';
    if (defect === 'encrypted-encoding') client.attributes['id.token.encrypted.response.enc'] = 'A256GCM';
    const before = clone(fixture.state);
    await assert.rejects(plan(fixture), error => error.reason === 'backchannel_conflict');
    assert.deepEqual(fixture.writes, []);
    assert.deepEqual(fixture.state, before);
  }
});

test('profile preparation cannot escape callback conflicts', async () => {
  const fixture = provider();
  fixture.state.profile.attributes = fixture.state.profile.attributes.filter(value => value.name !== 'user_id');
  fixture.state.profile.unmanagedAttributePolicy = 'DISABLED';
  web(fixture).attributes['backchannel.logout.url'] = 'https://foreign.invalid/logout';
  await assert.rejects(buildRealmPreparation({ admin: fixture.admin, desired: clone(desired), manifest: clone(manifest),
    webSecret: secret, backchannel }), error => error.reason === 'backchannel_conflict');
  assert.deepEqual(fixture.writes, []);
});

test('independent callback readback rejects lost fields, unrelated damage and denied writes', async () => {
  for (const defect of ['url', 'session', 'offline', 'frontchannel', 'api', 'denied']) {
    const fixture = provider();
    const put = fixture.admin.put;
    fixture.admin.put = async (...args) => { if (defect === 'denied') throw new Error('denied'); return put(...args); };
    const change = await plan(fixture);
    if (defect === 'denied') {
      await assert.rejects(change.actions[0].apply());
      assert.deepEqual(fixture.writes, []);
      continue;
    }
    for (const action of change.actions) await action.apply();
    const keys = { url: 'backchannel.logout.url', session: 'backchannel.logout.session.required', offline: 'backchannel.logout.revoke.offline.tokens' };
    if (keys[defect]) delete web(fixture).attributes[keys[defect]];
    if (defect === 'frontchannel') web(fixture).frontchannelLogout = true;
    if (defect === 'api') fixture.state.clients.find(value => value.clientId === 'learnstack-api').frontchannelLogout = false;
    await assert.rejects(change.verify(), error => error.reason === 'readback_conflict');
  }
});
