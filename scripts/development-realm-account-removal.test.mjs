import assert from 'node:assert/strict';
import test from 'node:test';
import { planAccountRemoval, planFederationAdmin, accountRemovalClient, federationAdminClient } from './development-event-reader.mjs';
import { reconcileRealm } from './development-realm-core.mjs';
import { desired, manifest, secret, federationSecret, accountRemovalSecret, provider, providerTransport } from './development-realm-test-fixture.mjs';
import { providerWithoutAccountRemoval } from './development-event-reader-test-fixture.mjs';

const clone = value => structuredClone(value);
const refusal = (stage, reason) => error => error.stage === stage && error.reason === reason;
const input = (transport, extra = {}) => ({ baseUrl: 'http://localhost:8080/', expectedIssuer: 'http://localhost:8080/realms/learnstack',
  adminUsername: 'fixture-admin', adminPassword: 'fixture-password', webSecret: secret, manifest: clone(manifest), desired: clone(desired),
  transport, ...extra });

// ADR-0107 section 7: the closure's account-removal client holds manage-users alone, confined like the federation client.

test('the committed import declares the account-removal client with manage-users alone and a placeholder secret', () => {
  const client = desired.clients.find(value => value.clientId === 'learnstack-account-removal');
  assert.equal(client.secret, '${KEYCLOAK_ACCOUNT_REMOVAL_SECRET}');
  assert.deepEqual(Object.entries(accountRemovalClient.fields).filter(([key, value]) => client[key] !== value
    && JSON.stringify(client[key]) !== JSON.stringify(value)), [], 'every declared field the planner verifies matches the export');
  assert.deepEqual(client.protocolMappers.map(mapper => mapper.name), ['learnstack-account-removal-roles']);
  const account = desired.users.find(user => user.serviceAccountClientId === 'learnstack-account-removal');
  assert.deepEqual(account.clientRoles, { 'realm-management': ['manage-users'] });
  assert.deepEqual(desired.clientScopeMappings['realm-management'].find(entry => entry.client === 'learnstack-account-removal').roles, ['manage-users']);
  assert.equal(accountRemovalClient.role, 'manage-users');
  assert.notEqual(accountRemovalClient.role, federationAdminClient.role);
});

test('the committed import already carries the client and plans no write', async () => {
  const fixture = provider();
  const plan = await planAccountRemoval(fixture.admin, accountRemovalSecret);
  assert.equal(plan.changed, false);
  await plan.verify();
  assert.deepEqual(fixture.writes, []);
  // The three machine clients are planned independently: the federation plan is unchanged.
  assert.equal((await planFederationAdmin(fixture.admin, federationSecret)).changed, false);
});

test('the account-removal client is created with only manage-users on a warm realm and then verifies', async () => {
  const fixture = providerWithoutAccountRemoval();
  const plan = await planAccountRemoval(fixture.admin, accountRemovalSecret);
  assert.equal(plan.changed, true);
  assert.deepEqual(fixture.writes, [], 'planning is read only');
  await plan.apply();
  await plan.verify();
  const grant = fixture.writes.filter(write => write.path.includes('role-mappings/clients') || write.path.includes('scope-mappings'));
  assert.deepEqual(grant.map(write => write.body.map(role => role.name)), [['manage-users'], ['manage-users']]);
  const created = fixture.writes.find(write => write.path === 'clients');
  assert.equal(created.body.secret, accountRemovalSecret);
  assert.equal(created.body.clientId, 'learnstack-account-removal');
  assert.equal(created.body.serviceAccountsEnabled, true);
  assert.equal(created.body.fullScopeAllowed, false);
  assert.equal(created.body.standardFlowEnabled, false);
  assert.deepEqual(created.body.protocolMappers.map(mapper => mapper.name), ['learnstack-account-removal-roles']);
  assert.equal((await planAccountRemoval(fixture.admin, accountRemovalSecret)).changed, false);
});

test('the full core creates the client, verifies it and makes the next pass zero-write', async () => {
  const fixture = providerWithoutAccountRemoval();
  const recording = providerTransport(fixture);
  const first = await reconcileRealm(input(recording.transport, { federationSecret, accountRemovalSecret }));
  assert.ok(first.mutations > 0);
  const created = fixture.state.clients.find(client => client.clientId === 'learnstack-account-removal');
  assert.equal(created.secret, accountRemovalSecret);
  assert.deepEqual(created.protocolMappers.map(mapper => mapper.name), ['learnstack-account-removal-roles']);
  assert.deepEqual(await reconcileRealm(input(recording.transport, { federationSecret, accountRemovalSecret })), { mutations: 0 });
});

test('without a secret the core plans no account-removal client at all, which is how a federation-only caller runs', async () => {
  const fixture = providerWithoutAccountRemoval();
  const recording = providerTransport(fixture);
  await reconcileRealm(input(recording.transport, { federationSecret }));
  assert.ok(!fixture.state.clients.some(client => client.clientId === 'learnstack-account-removal'));
});

for (const [name, mutate, reason] of [
  ['a different secret', fixture => { fixture.state.accountRemoval.client.secret = 'another-removal-secret-value'; }, 'secret_conflict'],
  ['full scope', fixture => { fixture.state.accountRemoval.client.fullScopeAllowed = true; }, 'readback_conflict'],
  ['a browser flow', fixture => { fixture.state.accountRemoval.client.standardFlowEnabled = true; }, 'readback_conflict'],
  ['direct grants', fixture => { fixture.state.accountRemoval.client.directAccessGrantsEnabled = true; }, 'readback_conflict'],
  ['a public client', fixture => { fixture.state.accountRemoval.client.publicClient = true; }, 'readback_conflict'],
  ['an extra mapper', fixture => { fixture.state.accountRemoval.client.protocolMappers.push({ ...fixture.state.accountRemoval.client.protocolMappers[0],
    id: 'extra', name: 'extra' }); }, 'mapper_conflict'],
  ['the federation role as a second grant', fixture => { fixture.state.accountRemoval.user.roles.clientMappings['realm-management'].mappings.push({
    ...fixture.state.federation.role }); }, 'unsupported'],
  ['a realm grant', fixture => { fixture.state.accountRemoval.user.roles.realmMappings = [{ name: 'admin' }]; }, 'unsupported'],
  ['a group membership', fixture => {
    const get = fixture.admin.get;
    fixture.admin.get = async path => path === `users/${fixture.state.accountRemoval.user.id}/groups?first=0&max=1`
      ? [{ id: 'g', name: 'g' }] : get(path);
  }, 'unsupported'],
]) test(`an existing account-removal client with ${name} refuses without a repair write`, async () => {
  const fixture = provider();
  mutate(fixture);
  await assert.rejects(planAccountRemoval(fixture.admin, accountRemovalSecret), refusal('plan', reason));
  assert.deepEqual(fixture.writes, []);
});

test('the federation client holding manage-users refuses, so neither client can drift into the other', async () => {
  const fixture = provider();
  fixture.state.federation.user.roles.clientMappings['realm-management'].mappings.push({ ...fixture.state.accountRemoval.role });
  await assert.rejects(planFederationAdmin(fixture.admin, federationSecret), refusal('plan', 'unsupported'));
  assert.deepEqual(fixture.writes, []);
});

for (const bad of [undefined, null, '', 'short', 'a-secret\nwith-a-newline-0123456789']) {
  test(`an unusable account-removal secret ${JSON.stringify(bad)} refuses before any read of the client`, async () => {
    const fixture = provider();
    await assert.rejects(planAccountRemoval(fixture.admin, bad), refusal('plan', 'configuration'));
    assert.deepEqual(fixture.reads.filter(path => path.includes('learnstack-account-removal')), []);
    assert.deepEqual(fixture.writes, []);
  });
}
