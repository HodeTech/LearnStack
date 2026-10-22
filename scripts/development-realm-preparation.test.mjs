import assert from 'node:assert/strict';
import test from 'node:test';
import { buildRealmPlan, buildRealmPreparation } from './development-realm-plan.mjs';
import { desired, manifest, secret, provider } from './development-realm-test-fixture.mjs';

const clone = value => structuredClone(value);
const claims = ['user_id', 'tenant_id', 'organization_id'];
const declared = JSON.parse(desired.components['org.keycloak.userprofile.UserProfileProvider'][0].config['kc.user.profile.config'][0]);
const input = fixture => ({ admin: fixture.admin, desired: clone(desired), manifest: clone(manifest), webSecret: secret });
const probe = fixture => buildRealmPreparation(input(fixture));
const refusal = (stage, reason) => error => error.stage === stage && error.reason === reason;

function hiddenProfile(missing = claims) {
  const fixture = provider();
  fixture.state.profile.attributes = fixture.state.profile.attributes.filter(attribute => !missing.includes(attribute.name));
  delete fixture.state.profile.unmanagedAttributePolicy;
  return fixture;
}

test('preparation returns only an exact absent-definition append and its independent verifier without applying provisional actions', async () => {
  const fixture = hiddenProfile();
  fixture.state.profile.retained = { fields: ['first', 'second'] };
  const original = clone(fixture.state);
  const preparation = await probe(fixture);
  assert.deepEqual(Object.keys(preparation).sort(), ['profile', 'verify']);
  const expected = clone(original.profile);
  expected.attributes.push(...claims.map(name => clone(declared.attributes.find(attribute => attribute.name === name))));
  assert.deepEqual(preparation.profile, expected);
  preparation.verify(clone(expected));
  assert.deepEqual(fixture.state, original);
  assert.deepEqual(fixture.writes, []);
});

test('the exact missing-definition and unmanaged-visibility trigger keeps all already-readable paths unchanged', async () => {
  for (const missing of [[], ['organization_id'], claims]) for (const policy of [undefined, null, 'DISABLED', 'ENABLED', 'ADMIN_EDIT', 'ADMIN_VIEW']) {
    const fixture = hiddenProfile(missing);
    if (policy !== undefined) fixture.state.profile.unmanagedAttributePolicy = policy;
    const preparation = await probe(fixture);
    const expected = missing.length > 0 && !['ENABLED', 'ADMIN_EDIT', 'ADMIN_VIEW'].includes(policy);
    assert.equal(Boolean(preparation), expected, `${missing.length}/${policy}`);
    if (!expected) assert.deepEqual(fixture.reads, ['users/profile']);
    assert.deepEqual(fixture.writes, []);
  }
});

test('a partial admin-view-only definition is preserved verbatim during preparation before separate ordinary normalization', async () => {
  const fixture = hiddenProfile(['user_id', 'organization_id']);
  const retained = fixture.state.profile.attributes.find(attribute => attribute.name === 'tenant_id');
  retained.permissions = { view: ['admin'], edit: [] };
  retained.multivalued = true;
  retained.required = { roles: ['user'] };
  const expected = clone(retained);
  const preparation = await probe(fixture);
  assert.deepEqual(preparation.profile.attributes.find(attribute => attribute.name === 'tenant_id'), expected);
  await fixture.admin.put('users/profile', preparation.profile);
  const check = await fixture.admin.get('users/profile');
  preparation.verify(check);
  const ordinary = await buildRealmPlan(input(fixture));
  assert.deepEqual(ordinary.actions.map(action => action.label), ['update_profile']);
  assert.equal(fixture.writes.length, 1);
  await ordinary.actions[0].apply();
  await ordinary.verify();
  assert.equal(fixture.writes.length, 2);
  assert.deepEqual((await buildRealmPlan(input(fixture))).actions, []);
});

test('hidden defaulted selected or unqualified existing protected definitions cannot be repaired by preparation', async () => {
  for (const variant of ['hidden', 'default', 'selector', 'unknown_field']) {
    const fixture = hiddenProfile(['organization_id']);
    const attribute = fixture.state.profile.attributes.find(attribute => attribute.name === 'user_id');
    if (variant === 'hidden') attribute.permissions = { view: ['user'], edit: ['user'] };
    if (variant === 'default') attribute.defaultValue = manifest.users[0].userId;
    if (variant === 'selector') attribute.selector = {};
    if (variant === 'unknown_field') attribute.unsupported = false;
    await assert.rejects(probe(fixture), refusal('prepare', 'identity_conflict'));
    assert.deepEqual(fixture.reads, ['users/profile']);
    assert.deepEqual(fixture.writes, []);
  }
});

test('new definitions must remain the separately declared admin-only scalar profile rather than a wider repair', async () => {
  for (const variant of ['view', 'edit', 'multivalued', 'default', 'selector', 'duplicate']) {
    const fixture = hiddenProfile();
    const value = input(fixture);
    const profile = clone(declared);
    const attribute = profile.attributes.find(attribute => attribute.name === 'user_id');
    if (variant === 'view') attribute.permissions.view.push('user');
    if (variant === 'edit') attribute.permissions.edit.push('user');
    if (variant === 'multivalued') attribute.multivalued = true;
    if (variant === 'default') attribute.defaultValue = manifest.users[0].userId;
    if (variant === 'selector') attribute.selector = {};
    if (variant === 'duplicate') profile.attributes.push(clone(attribute));
    value.desired.components['org.keycloak.userprofile.UserProfileProvider'][0].config['kc.user.profile.config'][0] = JSON.stringify(profile);
    await assert.rejects(buildRealmPreparation(value), refusal('prepare', 'identity_conflict'));
    assert.deepEqual(fixture.writes, []);
  }
});

test('a known ordinary account update cannot admit preparation before its shared profile-preservation guard', async () => {
  const clean = hiddenProfile(['organization_id']);
  clean.state.users[0].attributes.tenant_id = ['tenant-a'];
  const preparation = await probe(clean);
  await clean.admin.put('users/profile', preparation.profile);
  const ordinary = await buildRealmPlan(input(clean));
  assert.deepEqual(ordinary.actions.map(action => action.label), ['update_user_attributes']);
  await ordinary.actions[0].apply();
  await ordinary.verify();
  for (const projection of [{ defaultValue: 'synthesized' }, { selector: {} }]) {
    const dirty = hiddenProfile(['organization_id']);
    dirty.state.users[0].attributes.tenant_id = ['tenant-a'];
    dirty.state.profile.attributes.push({ name: 'retained',
      permissions: { view: ['admin'], edit: ['admin'] }, ...projection });
    await assert.rejects(probe(dirty), refusal('plan', 'unsupported'));
    assert.deepEqual(dirty.writes, []);
  }
});

test('an already-readable actor must qualify before preparation for another missing claim', async () => {
  assert.ok(await probe(hiddenProfile(['organization_id'])));
  for (const actor of [['01910000-0000-7000-8000-00000000ffff'], ['invalid'], null, []]) {
    const fixture = hiddenProfile(['organization_id']);
    // The Yoga identity has no legacy actor-absence exception.
    fixture.state.users[2].attributes.user_id = actor;
    await assert.rejects(probe(fixture), refusal('plan', 'identity_conflict'));
    assert.deepEqual(fixture.writes, []);
  }
});

test('readable legacy actor absence needs its readable legacy tenant before another claim can be prepared', async () => {
  const clean = hiddenProfile(['organization_id']);
  delete clean.state.users[0].attributes.user_id;
  clean.state.users[0].attributes.tenant_id = ['tenant-a'];
  assert.ok(await probe(clean));
  for (const tenant of [undefined, [manifest.users[0].tenantId], ['foreign']]) {
    const fixture = hiddenProfile(['organization_id']);
    delete fixture.state.users[0].attributes.user_id;
    if (tenant === undefined) delete fixture.state.users[0].attributes.tenant_id;
    else fixture.state.users[0].attributes.tenant_id = tenant;
    await assert.rejects(probe(fixture), refusal('plan', 'identity_conflict'));
    assert.deepEqual(fixture.writes, []);
  }
});

test('readable tenant and organization disagreements refuse before preparation of a different claim', async () => {
  for (const [missing, name, index] of [
    [['organization_id'], 'tenant_id', 0],
    [['user_id'], 'organization_id', 2],
    [['user_id'], 'organization_id', 0],
  ]) {
    assert.ok(await probe(hiddenProfile(missing)));
    for (const value of [['01910000-0000-7000-8000-00000000ffff'], ['invalid'], ['one', 'two']]) {
      const fixture = hiddenProfile(missing);
      fixture.state.users[index].attributes[name] = value;
      await assert.rejects(probe(fixture), refusal('plan', 'identity_conflict'));
      assert.deepEqual(fixture.writes, []);
    }
  }
});

test('already-readable actor duplication on a differently named account refuses before preparation', async () => {
  assert.ok(await probe(hiddenProfile(['organization_id'])));
  const fixture = hiddenProfile(['organization_id']);
  fixture.state.users.push({ id: 'unmanaged-existing', username: 'unmanaged-existing',
    attributes: { user_id: [manifest.users[0].userId] } });
  await assert.rejects(probe(fixture), refusal('plan', 'identity_conflict'));
  assert.deepEqual(fixture.writes, []);
});

test('only genuinely hidden identity conflicts wait for preparation and the fresh complete plan', async () => {
  const fixture = hiddenProfile(['user_id', 'tenant_id']);
  fixture.state.users[0].attributes.user_id = ['01910000-0000-7000-8000-00000000ffff'];
  const preparation = await probe(fixture);
  assert.deepEqual(fixture.writes, []);
  await fixture.admin.put('users/profile', preparation.profile);
  preparation.verify(await fixture.admin.get('users/profile'));
  await assert.rejects(buildRealmPlan(input(fixture)), refusal('plan', 'identity_conflict'));
  assert.equal(fixture.writes.length, 1);
});

const independentConflicts = [
  ['disabled realm', fixture => { fixture.state.realm.enabled = false; }, 'unsupported'],
  ['disabled client', fixture => { fixture.state.clients.find(client => client.clientId === 'learnstack-web').enabled = false; }, 'unsupported'],
  ['confidential secret', fixture => { fixture.state.clients.find(client => client.clientId === 'learnstack-web').secret = 'different-private-fixture'; }, 'secret_conflict'],
  ['service-account transition', fixture => { fixture.state.clients.find(client => client.clientId === 'learnstack-api').serviceAccountsEnabled = false; }, 'unsupported'],
  ['direct mapper', fixture => { fixture.state.clients.find(client => client.clientId === 'learnstack-web').protocolMappers.push({ id: 'unqualified', name: 'unqualified', protocol: 'openid-connect', protocolMapper: 'custom-unsafe', config: {} }); }, 'mapper_conflict'],
  ['scope protocol', fixture => { fixture.state.scopes.find(scope => scope.name === 'phone').protocol = 'saml'; }, 'unsupported'],
  ['console origin', fixture => { fixture.state.clients.find(client => client.clientId === 'account-console').rootUrl = 'https://foreign.fixture.invalid'; }, 'unsupported'],
  ['account composite', fixture => { fixture.state.roleChildren[fixture.state.accountRoles.find(role => role.name === 'manage-account').id] = []; }, 'unsupported'],
  ['external storage projection', fixture => { fixture.state.users[0].federationLink = 'external'; }, 'identity_conflict'],
  ['group identity source', fixture => { fixture.state.groups.push({ id: 'group', name: 'group', attributes: { user_id: [manifest.users[0].userId] } }); }, 'identity_conflict'],
  ['unknown membership source', fixture => { fixture.state.groups.push({ id: 'organization-group', name: 'organization-group', type: 'organization' }); fixture.state.memberships[fixture.state.users[0].id] = ['organization-group']; }, 'unsupported'],
  ['new account email collision', fixture => { const user = fixture.state.users.pop(); fixture.state.users.push({ ...user, id: 'unowned', username: 'unowned', attributes: {} }); }, 'identity_conflict'],
  ['new account role', fixture => { fixture.state.users.pop(); fixture.state.roles.find(role => role.name === manifest.users.at(-1).realmRole).composite = true; }, 'unsupported'],
  ['scope collection limit', fixture => { fixture.state.scopes.push(...Array.from({ length: 201 }, (_, index) => ({ id: `extra-${index}`, name: `extra-${index}`, protocol: 'openid-connect' }))); }, 'unsupported'],
  ['projected request capacity', fixture => { fixture.state.profile.attributes.push({ name: 'retained', permissions: { view: ['admin'], edit: ['admin'] } }); fixture.state.users[0].attributes.retained = ['x'.repeat(256 * 1024)]; }, 'body_limit'],
];

for (const [name, mutate, reason] of independentConflicts) {
  test(`the same ${name} qualifier refuses before a preparation write and has a clean executable twin`, async () => {
    const clean = hiddenProfile();
    assert.ok(await probe(clean));
    const fixture = hiddenProfile();
    mutate(fixture);
    const original = clone(fixture.state);
    await assert.rejects(probe(fixture), refusal('plan', reason));
    assert.deepEqual(fixture.state, original);
    assert.deepEqual(fixture.writes, []);
  });
}

test('projected metadata body and collection bounds bind before even a hidden-value preparation can write', async () => {
  for (const variant of ['bytes', 'attributes']) {
    const fixture = hiddenProfile();
    if (variant === 'bytes') fixture.state.profile.retained = 'x'.repeat(256 * 1024);
    else fixture.state.profile.attributes.push(...Array.from({ length: 195 }, (_, index) => ({ name: `retained-${index}` })));
    await assert.rejects(probe(fixture), error => error.reason === (variant === 'bytes' ? 'body_limit' : 'unsupported'));
    assert.deepEqual(fixture.writes, []);
  }
});

test('preparation verifier rejects complete-field and ordered-array changes without trusting its exposed body object', async () => {
  const fixture = hiddenProfile();
  const preparation = await probe(fixture);
  const expected = clone(preparation.profile);
  preparation.profile.attributes.reverse();
  assert.throws(() => preparation.verify(preparation.profile), refusal('prepare', 'readback_conflict'));
  const changed = clone(expected);
  changed.attributes.at(-1).permissions.view = ['admin', 'user'];
  assert.throws(() => preparation.verify(changed), refusal('prepare', 'readback_conflict'));
  preparation.verify(expected);
});
