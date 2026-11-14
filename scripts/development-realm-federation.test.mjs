import assert from 'node:assert/strict';
import test from 'node:test';
import { planFederationRealm, firstBrokerFlow, idpMapperName, testRecordScope } from './development-realm-federation.mjs';
import { planFederationAdmin, eventReaderDefaultRoleDetachment } from './development-event-reader.mjs';
import { reconcileRealm } from './development-realm-core.mjs';
import { desired, manifest, secret, federationSecret, provider, providerTransport } from './development-realm-test-fixture.mjs';
import { providerWithoutFederationAdmin } from './development-event-reader-test-fixture.mjs';

const clone = value => structuredClone(value);
const refusal = (stage, reason) => error => error.stage === stage && error.reason === reason;

// The realm as it stood before P03-8 Step 3: no link-only flow, idp_link enabled, no idp mapper.
function preStepThree(fixture = provider()) {
  fixture.state.flows = [];
  fixture.state.executions = [];
  fixture.state.requiredActions.find(action => action.alias === 'idp_link').enabled = true;
  const web = fixture.state.clients.find(client => client.clientId === 'learnstack-web');
  web.protocolMappers = web.protocolMappers.filter(mapper => mapper.name !== idpMapperName);
  return fixture;
}

const input = (transport, extra = {}) => ({ baseUrl: 'http://localhost:8080/', expectedIssuer: 'http://localhost:8080/realms/learnstack',
  adminUsername: 'fixture-admin', adminPassword: 'fixture-password', webSecret: secret, manifest: clone(manifest), desired: clone(desired),
  transport, ...extra });

test('the committed import already carries every federation invariant and plans no write', async () => {
  const fixture = provider();
  const plan = await planFederationRealm(fixture.admin, clone(desired));
  assert.equal(plan.changed, false);
  await plan.verify();
  assert.deepEqual(fixture.writes, []);
  assert.equal((await planFederationAdmin(fixture.admin, federationSecret)).changed, false);
});

test('a pre-Step-3 realm converges with the flow, the idp_link disable and the mapper, then reruns read only', async () => {
  const fixture = preStepThree();
  const plan = await planFederationRealm(fixture.admin, clone(desired));
  assert.equal(plan.actions, 3);
  assert.deepEqual(fixture.writes, [], 'planning is read only');
  await plan.apply();
  await plan.verify();
  assert.equal(fixture.state.requiredActions.find(action => action.alias === 'idp_link').enabled, false);
  assert.deepEqual(fixture.state.executions.map(execution => [execution.level, execution.index,
    execution.providerId ?? execution.displayName, execution.requirement]), [
    [0, 0, 'idp-detect-existing-broker-user', 'REQUIRED'], [0, 1, 'idp-confirm-link', 'REQUIRED'],
    [0, 2, 'learnstack-first-broker-reauthentication', 'REQUIRED'], [1, 0, 'idp-username-password-form', 'REQUIRED'],
    [1, 1, 'learnstack-first-broker-conditional-otp', 'CONDITIONAL'], [2, 0, 'conditional-user-configured', 'REQUIRED'],
    [2, 1, 'auth-otp-form', 'REQUIRED']]);
  assert.ok(!fixture.writes.some(write => /create|verify-email|auto-link|review-profile/.test(JSON.stringify(write.body ?? {}))),
    'no create-user, verify-email, auto-link or review-profile step is ever written');
  const writes = fixture.writes.length;
  assert.equal((await planFederationRealm(fixture.admin, clone(desired))).changed, false);
  assert.equal(fixture.writes.length, writes);
});

test('the full core converges a pre-Step-3 realm, verifies it and makes the next pass zero-write', async () => {
  const fixture = preStepThree(providerWithoutFederationAdmin());
  const recording = providerTransport(fixture);
  const first = await reconcileRealm(input(recording.transport, { federationSecret }));
  assert.ok(first.mutations > 3);
  const created = fixture.state.clients.find(client => client.clientId === 'learnstack-federation-admin');
  assert.equal(created.secret, federationSecret);
  assert.deepEqual(created.protocolMappers.map(mapper => mapper.name), ['learnstack-federation-admin-roles']);
  assert.deepEqual(await reconcileRealm(input(recording.transport, { federationSecret })), { mutations: 0 });
});

for (const [name, mutate] of [
  ['an alternative detector', fixture => { fixture.state.executions[0].requirement = 'ALTERNATIVE'; }],
  ['a create-user step', fixture => { fixture.state.executions.push({ id: 'x', level: 0, index: 3, providerId: 'idp-create-user-if-unique',
    displayName: 'Create', requirement: 'ALTERNATIVE' }); }],
  ['a verify-by-email step', fixture => { fixture.state.executions[3] = { ...fixture.state.executions[3], providerId: 'idp-email-verification' }; }],
  ['an optional OTP', fixture => { fixture.state.executions[4].requirement = 'DISABLED'; }],
  ['a built-in alias', fixture => { fixture.state.flows[0].builtIn = true; }],
]) test(`an existing first-broker flow with ${name} refuses flow_conflict without a write`, async () => {
  const fixture = provider();
  mutate(fixture);
  const before = clone(fixture.state);
  await assert.rejects(planFederationRealm(fixture.admin, clone(desired)), refusal('plan', 'flow_conflict'));
  assert.deepEqual(fixture.writes, []);
  assert.deepEqual(fixture.state, before);
});

test('an orphan subflow alias refuses creation rather than adopting it', async () => {
  const fixture = preStepThree();
  fixture.state.flows.push({ id: 'orphan', alias: 'learnstack-first-broker-reauthentication', providerId: 'basic-flow', topLevel: true, builtIn: false });
  await assert.rejects(planFederationRealm(fixture.admin, clone(desired)), refusal('plan', 'flow_conflict'));
  assert.deepEqual(fixture.writes, []);
});

for (const [name, mutate] of [
  ['an access-token idp claim', mapper => { mapper.config['access.token.claim'] = 'true'; }],
  ['another session note', mapper => { mapper.config['user.session.note'] = 'clientHost'; }],
]) test(`an owned idp mapper with ${name} refuses mapper_conflict`, async () => {
  const fixture = provider();
  mutate(fixture.state.clients.find(client => client.clientId === 'learnstack-web').protocolMappers
    .find(mapper => mapper.name === idpMapperName));
  await assert.rejects(planFederationRealm(fixture.admin, clone(desired)), refusal('plan', 'mapper_conflict'));
  assert.deepEqual(fixture.writes, []);
});

test('a second emitter of the idp claim refuses, so only the session note decides provenance', async () => {
  const fixture = provider();
  fixture.state.clients.find(client => client.clientId === 'learnstack-web').protocolMappers.push({ id: 'shadow', name: 'shadow-idp',
    protocol: 'openid-connect', protocolMapper: 'oidc-hardcoded-claim-mapper', consentRequired: false,
    config: { 'claim.name': 'idp', 'claim.value': 'lsf-forged', 'id.token.claim': 'true' } });
  await assert.rejects(planFederationRealm(fixture.admin, clone(desired)), refusal('plan', 'mapper_conflict'));
  assert.deepEqual(fixture.writes, []);
});

const scopeEmitter = (claim, name = 'scope-idp') => ({ id: `planted-${name}`, name, protocol: 'openid-connect',
  protocolMapper: 'oidc-hardcoded-claim-mapper', consentRequired: false,
  config: { 'claim.name': claim, 'claim.value': 'lsf-forged', 'id.token.claim': 'true' } });

for (const [kind, scopeName] of [['default', 'profile'], ['optional', 'phone']])
  test(`an idp emitter in a ${kind} client scope of learnstack-web refuses mapper_conflict`, async () => {
    const fixture = provider();
    const web = fixture.state.clients.find(client => client.clientId === 'learnstack-web');
    assert.ok((kind === 'default' ? web.defaultClientScopes : web.optionalClientScopes).includes(scopeName));
    fixture.state.scopes.find(scope => scope.name === scopeName).protocolMappers.push(scopeEmitter('idp'));
    await assert.rejects(planFederationRealm(fixture.admin, clone(desired)), refusal('plan', 'mapper_conflict'));
    assert.deepEqual(fixture.writes, []);
  });

test('a scope mapper carrying the canonical mapper name still refuses, and a nested idp root counts', async () => {
  for (const [name, claim] of [[idpMapperName, 'idp'], ['nested', 'idp.value']]) {
    const fixture = provider();
    fixture.state.scopes.find(scope => scope.name === 'roles').protocolMappers.push(scopeEmitter(claim, name));
    await assert.rejects(planFederationRealm(fixture.admin, clone(desired)), refusal('plan', 'mapper_conflict'));
  }
});

test('a client scope mapper emitting another claim, or a scope the client does not receive, does not refuse', async () => {
  const fixture = provider();
  fixture.state.scopes.find(scope => scope.name === 'profile').protocolMappers.push(scopeEmitter('idp_hint', 'other-claim'));
  const unassigned = fixture.state.scopes.find(scope => ![...fixture.state.clients.flatMap(client =>
    [...client.defaultClientScopes, ...client.optionalClientScopes])].includes(scope.name));
  assert.ok(unassigned, 'the fixture must keep a scope the client does not receive');
  unassigned.protocolMappers.push(scopeEmitter('idp', 'unassigned-idp'));
  const plan = await planFederationRealm(fixture.admin, clone(desired));
  assert.equal(plan.changed, false);
  await plan.verify();
  assert.deepEqual(fixture.writes, []);
});

test('an idp emitter that appears in a scope after planning fails the readback', async () => {
  const fixture = preStepThree();
  const plan = await planFederationRealm(fixture.admin, clone(desired));
  await plan.apply();
  fixture.state.scopes.find(scope => scope.name === 'email').protocolMappers.push(scopeEmitter('idp'));
  await assert.rejects(plan.verify(), refusal('readback', 'mapper_conflict'));
});

for (const [name, mutate, reason] of [
  ['a different secret', fixture => { fixture.state.federation.client.secret = 'another-federation-secret-value'; }, 'secret_conflict'],
  ['full scope', fixture => { fixture.state.federation.client.fullScopeAllowed = true; }, 'readback_conflict'],
  ['a browser flow', fixture => { fixture.state.federation.client.standardFlowEnabled = true; }, 'readback_conflict'],
  ['an extra grant', fixture => { fixture.state.federation.user.roles.clientMappings['realm-management'].mappings.push({
    ...fixture.state.federation.role, id: 'manage-users', name: 'manage-users' }); }, 'unsupported'],
  ['a realm grant', fixture => { fixture.state.federation.user.roles.realmMappings = [{ name: 'admin' }]; }, 'unsupported'],
]) test(`an existing federation admin client with ${name} refuses without a repair write`, async () => {
  const fixture = provider();
  mutate(fixture);
  await assert.rejects(planFederationAdmin(fixture.admin, federationSecret), refusal('plan', reason));
  assert.deepEqual(fixture.writes, []);
});

test('the federation admin client is created with only manage-identity-providers on a warm realm', async () => {
  const fixture = providerWithoutFederationAdmin();
  const plan = await planFederationAdmin(fixture.admin, federationSecret);
  assert.equal(plan.changed, true);
  assert.deepEqual(fixture.writes, []);
  await plan.apply();
  await plan.verify();
  const grant = fixture.writes.filter(write => write.path.includes('role-mappings/clients') || write.path.includes('scope-mappings'));
  assert.deepEqual(grant.map(write => write.body.map(role => role.name)), [['manage-identity-providers'], ['manage-identity-providers']]);
  assert.equal((await planFederationAdmin(fixture.admin, federationSecret)).changed, false);
});

test('the flow name is the one the fixed broker profile names', () => {
  assert.equal(firstBrokerFlow, 'learnstack-first-broker-login');
  assert.ok(desired.authenticationFlows.some(flow => flow.alias === firstBrokerFlow));
  assert.equal(desired.requiredActions.find(action => action.alias === 'idp_link').enabled, false);
});

test('a newly created service account leaves the realm default groups; an existing one in a group refuses', async () => {
  const fixture = providerWithoutFederationAdmin();
  let joined = [{ id: 'realm-default-group', name: 'Default' }];
  const removed = [];
  const get = fixture.admin.get;
  fixture.admin.get = async path => /\/groups\?first=0&max=100$/.test(path) ? structuredClone(joined) : get(path);
  fixture.admin.detachNewEventReaderDefault = async binding => {
    const value = eventReaderDefaultRoleDetachment(binding);
    if (!/\/groups\//.test(value.path)) return fixture.admin.delete(value.path, value.body);
    removed.push(value.path);
    joined = [];
  };
  const plan = await planFederationAdmin(fixture.admin, federationSecret);
  await plan.apply();
  assert.deepEqual(removed.map(path => path.split('/').at(-1)), ['realm-default-group']);
  // Dirty control: an existing account in a group is never repaired.
  const existing = provider();
  const existingGet = existing.admin.get;
  existing.admin.get = async path => path === `users/${existing.state.federation.user.id}/groups?first=0&max=1`
    ? [{ id: 'g', name: 'g' }] : existingGet(path);
  await assert.rejects(planFederationAdmin(existing.admin, federationSecret), refusal('plan', 'unsupported'));
  assert.deepEqual(existing.writes, []);
});

// ADR-0099 Amendment 6: the test-record scope is optional only and carries no mapper.
function preStepFour(fixture = provider()) {
  fixture.state.scopes = fixture.state.scopes.filter(scope => scope.name !== testRecordScope);
  const web = fixture.state.clients.find(client => client.clientId === 'learnstack-web');
  web.optionalClientScopes = web.optionalClientScopes.filter(name => name !== testRecordScope);
  return fixture;
}

test('the committed import declares the test-record scope optional on learnstack-web with no mapper', async () => {
  const scope = desired.clientScopes.find(value => value.name === testRecordScope);
  assert.deepEqual(scope.protocolMappers ?? [], []);
  assert.equal(scope.attributes['include.in.token.scope'], 'true');
  const web = desired.clients.find(client => client.clientId === 'learnstack-web');
  assert.ok(web.optionalClientScopes.includes(testRecordScope));
  assert.ok(!web.defaultClientScopes.includes(testRecordScope));
});

test('a pre-Step-4 realm gains the optional test-record scope through the core, then reruns zero-write', async () => {
  const fixture = preStepFour(providerWithoutFederationAdmin());
  const recording = providerTransport(fixture);
  const first = await reconcileRealm(input(recording.transport, { federationSecret }));
  assert.ok(first.mutations >= 2, 'the scope is created and attached as optional');
  const web = fixture.state.clients.find(client => client.clientId === 'learnstack-web');
  assert.ok(web.optionalClientScopes.includes(testRecordScope));
  assert.ok(!web.defaultClientScopes.includes(testRecordScope));
  assert.deepEqual(fixture.state.scopes.find(scope => scope.name === testRecordScope).protocolMappers, []);
  assert.deepEqual(await reconcileRealm(input(recording.transport, { federationSecret })), { mutations: 0 });
});

test('the test-record scope as a default of learnstack-web refuses mapper_conflict without a write', async () => {
  const fixture = provider();
  const web = fixture.state.clients.find(client => client.clientId === 'learnstack-web');
  web.optionalClientScopes = web.optionalClientScopes.filter(name => name !== testRecordScope);
  web.defaultClientScopes.push(testRecordScope);
  await assert.rejects(planFederationRealm(fixture.admin, clone(desired)), refusal('plan', 'mapper_conflict'));
  assert.deepEqual(fixture.writes, []);
});

test('a mapper planted in the test-record scope refuses mapper_conflict, whatever claim it emits', async () => {
  for (const claim of ['idp', 'other']) {
    const fixture = provider();
    fixture.state.scopes.find(scope => scope.name === testRecordScope).protocolMappers.push(scopeEmitter(claim, `planted-${claim}`));
    await assert.rejects(planFederationRealm(fixture.admin, clone(desired)), refusal('plan', 'mapper_conflict'));
    assert.deepEqual(fixture.writes, []);
  }
});

test('an existing test-record scope with a differing definition refuses in the core plan rather than being adopted', async () => {
  const fixture = providerWithoutFederationAdmin();
  fixture.state.scopes.find(scope => scope.name === testRecordScope).attributes['include.in.token.scope'] = 'false';
  const recording = providerTransport(fixture);
  await assert.rejects(reconcileRealm(input(recording.transport, { federationSecret })), refusal('plan', 'mapper_conflict'));
});

for (const [name, mutate] of [
  ['without the scope', value => { value.clientScopes = value.clientScopes.filter(scope => scope.name !== testRecordScope); }],
  ['with the scope as a default', value => {
    value.clients.find(client => client.clientId === 'learnstack-web').defaultClientScopes.push(testRecordScope); }],
  ['with a scope mapper', value => { value.clientScopes.find(scope => scope.name === testRecordScope).protocolMappers =
    [scopeEmitter('other', 'declared')]; }],
]) test(`a desired export ${name} refuses unsupported before any read`, async () => {
  const fixture = provider();
  const changed = clone(desired);
  mutate(changed);
  await assert.rejects(planFederationRealm(fixture.admin, changed), refusal('plan', 'unsupported'));
  assert.deepEqual(fixture.writes, []);
});
