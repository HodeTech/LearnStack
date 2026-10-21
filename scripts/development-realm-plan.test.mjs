import test from 'node:test';
import assert from 'node:assert/strict';
import { buildRealmPlan } from './development-realm-plan.mjs';
import { desired, manifest, secret, provider } from './development-realm-test-fixture.mjs';
import { accountMapperRepresentation, claimRoot, validateAccountMappers, validateEffectiveMappers } from './development-realm-mappers.mjs';

const clone = value => structuredClone(value);
const omit = (value, keys) => Object.fromEntries(Object.entries(value).filter(([key]) => !keys.includes(key)));

const plan = fixture => buildRealmPlan({ admin: fixture.admin, desired: clone(desired), manifest: clone(manifest), webSecret: secret });
const apply = async change => { for (const action of change.actions) await action.apply(); await change.verify(); };

for (const [name, mutate, reason = 'identity_conflict'] of [
  ['wrong realm', input => { input.desired.realm = 'foreign'; }, 'unsupported'],
  ['wrong lifespan', input => { input.desired.accessTokenLifespan = 1800; }, 'unsupported'],
  ['missing exported users', input => { input.desired.users = null; }, 'unsupported'],
  ['wrong exported count', input => { input.desired.users.pop(); }, 'unsupported'],
  ['missing manifest users', input => { input.manifest.users = null; }, 'unsupported'],
  ['wrong manifest count', input => { input.manifest.users.pop(); }, 'unsupported'],
  ['empty secret', input => { input.webSecret = ''; }, 'unsupported'],
  ['nonstring secret', input => { input.webSecret = 123; }, 'unsupported'],
  ['duplicate username', input => { input.manifest.users[1].username = input.manifest.users[0].username; }],
  ['invalid actor UUID', input => { input.manifest.users[0].userId = 'invalid'; }],
  ['reserved actor UUID', input => { input.manifest.users[0].userId = '00000000-0000-0000-0000-000000000000'; }],
  ['non-v7 actor UUID', input => { input.manifest.users[0].userId = input.manifest.users[0].userId.replace('-7000-', '-4000-'); }],
  ['duplicate actor', input => { input.manifest.users[1].userId = input.manifest.users[0].userId; }],
  ['invalid tenant UUID', input => { input.manifest.users[0].tenantId = 'invalid'; }],
  ['invalid organization UUID', input => { input.manifest.users[2].organizationId = 'invalid'; }],
  ['tenant organization collision', input => { input.manifest.users[2].organizationId = input.manifest.users[2].tenantId; }],
  ['actor scope collision', input => { input.manifest.users[0].userId = input.manifest.users[2].organizationId; }],
  ['wrong legacy literal', input => { input.manifest.users[0].legacyTenantAttribute = 'another-legacy'; }],
  ['Yoga legacy migration', input => { input.manifest.users[2].legacyTenantAttribute = 'tenant-a'; }],
  ['legacy organization', input => { input.manifest.users[0].organizationId = input.manifest.users[2].organizationId; }],
  ['wrong account capability', input => { input.manifest.users[0].accountCapability.role = 'view-profile'; }],
  ['missing exported persona', input => { input.desired.users[0].username = 'other'; }],
  ['duplicate exported persona', input => { input.desired.users[1].username = input.desired.users[0].username; }],
  ['wrong email', input => { input.desired.users[0].email = 'other@fixture.invalid'; }],
  ['wrong first name', input => { input.desired.users[0].firstName = 'other'; }],
  ['wrong last name', input => { input.desired.users[0].lastName = 'other'; }],
  ['disabled declaration', input => { input.desired.users[0].enabled = false; }],
  ['unverified email', input => { input.desired.users[0].emailVerified = false; }],
  ['missing credentials', input => { input.desired.users[0].credentials = []; }],
  ['null credential', input => { input.desired.users[0].credentials = [null]; }],
  ['multiple credentials', input => { input.desired.users[0].credentials.push(clone(input.desired.users[0].credentials[0])); }],
  ['wrong credential type', input => { input.desired.users[0].credentials[0].type = 'otp'; }],
  ['temporary credential', input => { input.desired.users[0].credentials[0].temporary = true; }],
  ['empty credential', input => { input.desired.users[0].credentials[0].value = ''; }],
  ['wrong declared role', input => { input.manifest.users[0].realmRole = 'unknown'; }],
  ['wrong exported role', input => { input.desired.users[0].realmRoles = ['tenant-learner']; }],
  ['wrong account grant', input => { input.desired.users[0].clientRoles.account = ['view-profile']; }],
  ['wrong exported actor', input => { input.desired.users[0].attributes.user_id = [input.manifest.users[1].userId]; }],
]) {
  test(`input preflight rejects ${name} before the first provider read`, async () => {
    const fixture = provider();
    const input = { desired: clone(desired), manifest: clone(manifest), webSecret: secret, admin: fixture.admin };
    mutate(input);
    await assert.rejects(buildRealmPlan(input), error => error.stage === 'plan' && error.reason === reason);
    assert.deepEqual(fixture.reads, []);
    assert.deepEqual(fixture.writes, []);
  });
}

test('disabled realm or named client and consent drift refuse without repairing unrelated settings', async () => {
  for (const resource of ['realm', 'learnstack-web', 'learnstack-api']) {
    for (const field of resource === 'realm' ? ['enabled'] : ['enabled', 'consentRequired']) {
      const fixture = provider();
      const value = resource === 'realm' ? fixture.state.realm : fixture.state.clients.find(client => client.clientId === resource);
      value[field] = field !== 'enabled';
      const original = clone(fixture.state);
      await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'unsupported');
      assert.deepEqual(fixture.state, original);
      assert.deepEqual(fixture.writes, []);
    }
  }
});

test('stock account callbacks, origins and consent must match the supported warm boundary', async () => {
  for (const name of ['account', 'account-console']) for (const [field, value] of [
    ['redirectUris', ['https://foreign.fixture.invalid/*']], ['webOrigins', ['*']],
    ['rootUrl', 'https://foreign.fixture.invalid/'], ['baseUrl', '/foreign/'],
    ['consentRequired', true], ['attributes', { 'post.logout.redirect.uris': '*' }],
  ]) {
    const fixture = provider();
    const client = fixture.state.clients.find(client => client.clientId === name);
    client[field] = field === 'attributes' ? { ...client.attributes, ...value } : clone(value);
    const original = clone(fixture.state);
    await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'unsupported', `${name}/${field}`);
    assert.deepEqual(fixture.writes, []);
    assert.deepEqual(fixture.state, original);
  }
});

test('matching reserved usernames without protected actor identity are not adopted from public registration', async () => {
  for (const index of [0, 1, 2, 3]) for (const actor of [undefined, []]) {
    const fixture = provider();
    if (actor === undefined) delete fixture.state.users[index].attributes.user_id;
    else fixture.state.users[index].attributes.user_id = actor;
    const original = clone(fixture.state);
    await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'identity_conflict');
    assert.deepEqual(fixture.writes, []);
    assert.deepEqual(fixture.state, original);
  }
  const legacyFixture = provider(); legacy(legacyFixture);
  await apply(await plan(legacyFixture));
  for (const declaration of manifest.users) assert.deepEqual(legacyFixture.state.users
    .find(user => user.username === declaration.username).attributes.user_id, [declaration.userId]);
});

test('equivalent redirect registration order requires no write and preserves its existing representation', async () => {
  const fixture = provider();
  fixture.state.clients.find(client => client.clientId === 'learnstack-web').redirectUris.reverse();
  const original = clone(fixture.state);
  const change = await plan(fixture);
  assert.deepEqual(change.actions, []);
  await change.verify();
  assert.deepEqual(fixture.state, original);
});

test('unsafe owned callbacks and password grants are repaired independently without changing retained client state', async () => {
  for (const name of ['learnstack-web', 'learnstack-api']) {
    for (const [field, unsafe] of [['redirectUris', ['http://localhost:3000/*', 'https://foreign.fixture.invalid/*']],
      ['directAccessGrantsEnabled', true]]) {
      for (const loseField of [false, true]) {
        const fixture = provider();
        const client = fixture.state.clients.find(value => value.clientId === name);
        client[field] = clone(unsafe);
        client.description = 'retained client description';
        client.attributes['retained.fixture.setting'] = 'retained';
        const before = clone(client);
        const change = await plan(fixture);
        assert.deepEqual(change.actions.map(action => action.label), ['update_client']);
        assert.deepEqual(fixture.writes, []);
        const put = fixture.admin.put;
        fixture.admin.put = async (path, body) => put(path, loseField ? omit(body, [field]) : body);
        if (loseField) {
          await assert.rejects(apply(change), error => error.stage === 'readback' && error.reason === 'readback_conflict');
          assert.deepEqual(client[field], unsafe);
        } else {
          await apply(change);
          assert.deepEqual(client[field], desired.clients.find(value => value.clientId === name)[field]);
          assert.deepEqual(omit(client, [field, 'authorizationServicesEnabled']), omit(before, [field, 'authorizationServicesEnabled']));
          assert.equal((await plan(fixture)).actions.length, 0);
          assert.equal(fixture.writes.length, 1);
        }
        assert.equal(fixture.writes[0].path, `clients/${client.id}`);
      }
    }
  }
});

test('exact username lookups distinguish retained near matches during both existing-user and new-user reconciliation', async () => {
  for (const creating of [false, true]) {
    const fixture = provider();
    const declaration = manifest.users.at(-1);
    if (creating) fixture.state.users.pop();
    const near = { id: 'retained-near-match', username: `prefix-${declaration.username}-suffix`,
      enabled: false, attributes: {}, credentials: [], roles: {} };
    fixture.state.users.unshift(near);
    const lookup = `users?username=${encodeURIComponent(declaration.username)}&max=2&briefRepresentation=false`;
    const contains = await fixture.admin.get(lookup);
    assert.equal(contains.length, creating ? 1 : 2, 'the independent provider model must expose the contains-match distinction');
    const exact = await fixture.admin.get(lookup + '&exact=true');
    assert.equal(exact.length, creating ? 0 : 1);
    const before = clone(near);
    const change = await plan(fixture);
    assert.deepEqual(change.actions.map(action => action.label), creating ? ['create_user'] : []);
    await apply(change);
    assert.deepEqual(near, before);
    assert.equal(fixture.state.users.filter(user => user.username === declaration.username).length, 1);
    assert.equal((await plan(fixture)).actions.length, 0);
  }
});

test('full filtered census exposes actor collisions on both ordinary and service accounts before pending writes', async () => {
  for (const serviceAccount of [false, true]) {
    const fixture = provider();
    fixture.state.realm.accessTokenLifespan = 1800;
    const other = { id: 'retained-collision', username: 'retained-collision', enabled: false,
      attributes: { user_id: [manifest.users[0].userId] }, credentials: [], roles: {},
      ...(serviceAccount ? { serviceAccountClientId: 'learnstack-api' } : {}) };
    fixture.state.users.push(other);
    const path = 'users?first=0&max=100';
    const unfiltered = await fixture.admin.get(path);
    assert.equal(unfiltered.some(user => user.id === other.id), !serviceAccount);
    const brief = await fixture.admin.get(path + '&exact=false&briefRepresentation=true');
    assert.equal(brief.find(user => user.id === other.id).attributes, undefined);
    const full = await fixture.admin.get(path + '&exact=false&briefRepresentation=false');
    assert.deepEqual(full.find(user => user.id === other.id).attributes.user_id, [manifest.users[0].userId]);
    assert.deepEqual(await fixture.admin.get(path + '&exact=false'), full,
      'omitting explicit brief=false is equivalent in the pinned provider and is not a defect');
    await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'identity_conflict');
    assert.deepEqual(fixture.writes, []);
    delete other.attributes.user_id;
    const before = clone(other);
    await apply(await plan(fixture));
    assert.deepEqual(other, before);
    assert.equal((await plan(fixture)).actions.length, 0);
  }
});

test('successful user import receipts cannot conceal independently corrupted created account data or access', async () => {
  const corruptions = [
    user => { user.username = 'different-created-user'; },
    user => { user.email = 'different@fixture.invalid'; },
    user => { user.enabled = false; },
    user => { user.emailVerified = false; },
    user => { user.firstName = 'different'; },
    user => { user.lastName = 'different'; },
    user => { user.attributes.user_id = [manifest.users[0].userId]; },
    user => { user.attributes.tenant_id = [manifest.users[0].tenantId]; },
    user => { user.requiredActions = ['UPDATE_PASSWORD']; },
    user => { user.roles.realmMappings[0].id = 'different-role-id'; },
    user => { user.roles.clientMappings.account.mappings = []; },
    user => { user.credentials = []; },
    user => { user.credentials[0].type = 'otp'; },
    user => { user.credentials.push({ id: 'extra-credential', type: 'password' }); },
  ];
  for (const corrupt of corruptions) {
    const fixture = provider(); fixture.state.users.pop();
    const post = fixture.admin.post;
    fixture.admin.post = async (path, body) => {
      const response = await post(path, body);
      assert.equal(path, 'partialImport');
      corrupt(fixture.state.users.at(-1));
      return response;
    };
    const change = await plan(fixture);
    assert.deepEqual(change.actions.map(action => action.label), ['create_user']);
    await assert.rejects(change.actions[0].apply(), error => error.stage === 'apply' && error.reason === 'identity_conflict');
    assert.equal(fixture.writes.length, 1);
    assert.equal(fixture.state.users.length, 4, 'refusal preserves the uncertain durable account instead of deleting or retrying it');
  }
});

test('readback rechecks newly created accounts after their successful immediate import verification', async () => {
  for (const corrupt of [
    user => { user.firstName = 'changed-after-creation'; },
    user => { user.attributes.organization_id = [manifest.users[0].tenantId]; },
    user => { user.requiredActions = ['UPDATE_PASSWORD']; },
    user => { user.roles.realmMappings.push({ id: 'extra-role', name: 'extra', composite: false, clientRole: false }); },
    user => { user.credentials[0].type = 'otp'; },
  ]) {
    const fixture = provider(); fixture.state.users.pop();
    const change = await plan(fixture);
    for (const action of change.actions) await action.apply();
    await change.verify();
    corrupt(fixture.state.users.at(-1));
    await assert.rejects(change.verify(), error => error.stage === 'readback');
    assert.equal(fixture.writes.length, 1, 'readback never repairs a mismatching account behind the operator');
  }
});

test('warm owned scope attributes and mapper destinations converge and independently verify', async () => {
  const fixture = provider();
  const scope = fixture.state.scopes.find(value => value.name === 'learnstack-tenant-api');
  scope.attributes['include.in.token.scope'] = 'false';
  scope.attributes.retained = 'preserved';
  scope.protocolMappers[0].config['userinfo.token.claim'] = 'true';
  const change = await plan(fixture);
  assert.deepEqual(change.actions.map(action => action.label), ['update_mapper', 'update_scope']);
  await apply(change);
  assert.equal(scope.attributes.retained, 'preserved');
  assert.equal(scope.attributes['include.in.token.scope'], 'true');
  assert.equal(scope.protocolMappers[0].config['userinfo.token.claim'], 'false');
  assert.equal((await plan(fixture)).actions.length, 0);
});

test('scope repair carries the bounded provider envelope and an omitted-name control cannot mutate state', async () => {
  for (const omitName of [false, true]) {
    const fixture = provider();
    const scope = fixture.state.scopes.find(value => value.name === 'learnstack-tenant-api');
    scope.attributes['include.in.token.scope'] = 'false';
    scope.attributes.retained = 'preserved';
    scope.description = 'retained administrator description';
    const original = clone(scope);
    const change = await plan(fixture);
    assert.deepEqual(change.actions.map(action => action.label), ['update_scope']);
    const put = fixture.admin.put;
    let sent;
    fixture.admin.put = (path, body) => {
      sent = clone(body);
      if (omitName) delete body.name;
      return put(path, body);
    };
    if (omitName) {
      await assert.rejects(apply(change), /pinned scope update requires unchanged name/);
      assert.deepEqual(scope, original);
    } else {
      await apply(change);
      assert.equal(scope.description, original.description);
      assert.equal(scope.attributes.retained, 'preserved');
      assert.equal((await plan(fixture)).actions.length, 0);
    }
    assert.deepEqual(Object.keys(sent).sort(), ['attributes', 'name', 'protocol']);
    assert.equal(sent.name, original.name);
    assert.equal(sent.protocol, original.protocol);
  }
});

test('user creation requires the exact successful ADDED receipt bound to its new provider ID', async () => {
  for (const mutate of [
    response => { delete response.body; }, response => { response.body.added = 0; },
    response => { response.body.overwritten = 1; }, response => { response.body.skipped = 1; },
    response => { response.body.results[0].action = 'SKIPPED'; },
    response => { response.body.results[0].resourceType = 'CLIENT'; },
    response => { response.body.results[0].resourceName = 'other'; },
    response => { response.body.results[0].id = 'different-provider-id'; },
    response => { response.body.results.push(clone(response.body.results[0])); },
    response => { response.body.results = [null]; },
  ]) {
    const fixture = provider(); fixture.state.users.pop();
    const post = fixture.admin.post;
    fixture.admin.post = async (path, body) => { const response = await post(path, body); mutate(response); return response; };
    const change = await plan(fixture);
    await assert.rejects(change.actions[0].apply(), error => error.stage === 'apply' && error.reason === 'identity_conflict');
    assert.equal(fixture.writes.length, 1);
    assert.equal(fixture.writes[0].body.ifResourceExists, 'FAIL');
  }
});

test('identity and audience mapper destinations reject independent unsafe configurations', () => {
  const effective = desired.clientScopes.filter(scope => ['basic', 'roles', 'learnstack-tenant-api'].includes(scope.name))
    .flatMap(scope => scope.protocolMappers ?? []);
  validateEffectiveMappers(effective);
  for (const [type, field, value] of [
    ['oidc-usermodel-attribute-mapper', 'access.token.claim', 'false'],
    ['oidc-usermodel-attribute-mapper', 'id.token.claim', 'false'],
    ['oidc-usermodel-attribute-mapper', 'userinfo.token.claim', 'true'],
    ['oidc-usermodel-attribute-mapper', 'jsonType.label', 'long'],
    ['oidc-usermodel-attribute-mapper', 'multivalued', 'true'],
    ['oidc-audience-mapper', 'id.token.claim', 'true'],
    ['oidc-audience-mapper', 'access.token.claim', 'false'],
    ['oidc-audience-resolve-mapper', 'id.token.claim', 'true'],
    ['oidc-sub-mapper', 'access.token.claim', 'false'],
  ]) {
    const values = clone(effective);
    values.find(mapper => mapper.protocolMapper === type).config[field] = value;
    assert.throws(() => validateEffectiveMappers(values), error => error.reason === 'mapper_conflict', `${type}/${field}`);
  }
});

test('concurrent scope and mapper creation never admits extra provider configuration', async () => {
  for (const resource of ['scope', 'mapper']) {
    const fixture = provider();
    const scope = fixture.state.scopes.find(scope => scope.name === 'basic');
    if (resource === 'scope') {
      fixture.state.scopes = fixture.state.scopes.filter(scope => scope.name !== 'learnstack-tenant-api');
      for (const client of fixture.state.clients) client.defaultClientScopes = client.defaultClientScopes.filter(name => name !== 'learnstack-tenant-api');
    } else scope.protocolMappers = scope.protocolMappers.filter(mapper => mapper.protocolMapper !== 'oidc-sub-mapper');
    const post = fixture.admin.post;
    fixture.admin.post = async (path, body) => {
      await post(path, body);
      if (resource === 'scope') fixture.state.scopes.at(-1).attributes.unexpected = 'not-admitted';
      else scope.protocolMappers.at(-1).config.unexpected = 'not-admitted';
      return { status: 409 };
    };
    const change = await plan(fixture);
    await assert.rejects(change.actions[0].apply(), error => error.stage === 'apply' && error.reason === 'mapper_conflict');
    assert.equal(fixture.writes.length, 1);
  }
});

function legacy(fixture) {
  const { state } = fixture;
  state.realm.accessTokenLifespan = 1800;
  state.profile.attributes = state.profile.attributes.filter(attribute => !['user_id', 'tenant_id', 'organization_id'].includes(attribute.name));
  state.profile.unmanagedAttributePolicy = 'ADMIN_EDIT';
  state.profile.attributes.push({ name: 'retained', permissions: { view: ['admin'], edit: ['admin'] } });
  state.profile.groups.push({ name: 'retained', displayHeader: 'Retained group' });
  state.scopes = state.scopes.filter(scope => scope.name !== 'learnstack-tenant-api');
  for (const client of state.clients.filter(client => ['learnstack-web', 'learnstack-api'].includes(client.clientId))) {
    client.defaultClientScopes = client.defaultClientScopes.filter(name => name !== 'learnstack-tenant-api');
    client.attributes['access.token.lifespan'] = '1800';
    client.attributes['retained-setting'] = 'retained';
    if (client.clientId === 'learnstack-web') { client.publicClient = true; client.secret = 'unused-public-secret'; }
  }
  state.clients.find(client => client.clientId === 'account-console').protocolMappers =
    state.clients.find(client => client.clientId === 'account-console').protocolMappers.filter(mapper => mapper.protocolMapper !== 'oidc-usermodel-client-role-mapper');
  state.users = state.users.slice(0, 2);
  for (const [index, user] of state.users.entries()) {
    user.attributes = { tenant_id: [manifest.users[index].legacyTenantAttribute], retained: ['untouched'] };
    user.enabled = false;
    user.requiredActions = ['UPDATE_PASSWORD'];
    user.credentials.push({ id: `mfa-${index}`, type: 'otp', credentialData: 'retained MFA' });
    user.firstName = 'Retained';
    delete user.roles.clientMappings;
  }
  state.users.push({ id: 'unrelated-account', username: 'unrelated-account', enabled: true,
    attributes: { retained: ['untouched'] }, credentials: [], roles: {} });
}

test('fresh desired state has zero actions and independently verifies', async () => {
  const fixture = provider();
  const change = await plan(fixture);
  assert.equal(change.actions.length, 0);
  assert.equal(fixture.writes.length, 0);
  await change.verify();
  assert.equal(fixture.writes.length, 0);
});

test('pinned mapper storage omits an empty account role prefix while qualification accepts precisely empty or absent', async () => {
  const canonical = desired.clients.find(client => client.clientId === 'account-console').protocolMappers
    .find(mapper => mapper.protocolMapper === 'oidc-usermodel-client-role-mapper');
  const prefix = 'usermodel.clientRoleMapping.rolePrefix';
  assert.equal(canonical.config[prefix], '');
  const fixture = provider();
  const mappers = fixture.state.clients.find(client => client.clientId === 'account-console').protocolMappers;
  const stored = mappers.find(mapper => mapper.protocolMapper === canonical.protocolMapper);
  assert.equal(Object.hasOwn(stored.config, prefix), false, 'provider state is not an echo of the export');
  assert.deepEqual(omit(stored, ['id']), accountMapperRepresentation(canonical));
  for (const value of [undefined, '']) {
    if (value === undefined) delete stored.config[prefix];
    else stored.config[prefix] = value;
    const before = clone(fixture.state);
    assert.equal(validateAccountMappers(mappers, canonical), true);
    const change = await plan(fixture);
    assert.equal(change.actions.length, 0);
    await change.verify();
    assert.deepEqual(fixture.state, before);
  }
});

test('account prefix normalization never drops unknown configuration or supplies another missing required field', async () => {
  const canonical = desired.clients.find(client => client.clientId === 'account-console').protocolMappers
    .find(mapper => mapper.protocolMapper === 'oidc-usermodel-client-role-mapper');
  const mutations = Object.keys(canonical.config).filter(key => key !== 'usermodel.clientRoleMapping.rolePrefix')
    .map(key => config => { delete config[key]; });
  for (const value of [null, ' ', 'unexpected-', false]) mutations.push(config => { config['usermodel.clientRoleMapping.rolePrefix'] = value; });
  mutations.push(config => { config['unqualified.empty.setting'] = ''; });
  for (const mutate of mutations) {
    const fixture = provider(); fixture.state.realm.accessTokenLifespan = 1800;
    const mapper = fixture.state.clients.find(client => client.clientId === 'account-console').protocolMappers
      .find(mapper => mapper.protocolMapper === canonical.protocolMapper);
    mutate(mapper.config);
    const before = clone(fixture.state);
    await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'mapper_conflict');
    assert.equal(fixture.writes.length, 0);
    assert.deepEqual(fixture.state, before);
  }
});

test('new account mapper readback accepts only the pinned empty-prefix omission, including one exact creation conflict check', async () => {
  for (const status of [201, 409]) for (const corrupt of [false, true]) {
    const fixture = provider();
    const console = fixture.state.clients.find(client => client.clientId === 'account-console');
    console.protocolMappers = console.protocolMappers.filter(mapper => mapper.protocolMapper !== 'oidc-usermodel-client-role-mapper');
    const originalPost = fixture.admin.post;
    fixture.admin.post = async (path, body) => {
      await originalPost(path, body);
      if (corrupt) console.protocolMappers.at(-1).config['id.token.claim'] = 'true';
      return { status };
    };
    const change = await plan(fixture);
    assert.deepEqual(change.actions.map(action => action.label), ['create_account_mapper']);
    if (corrupt) await assert.rejects(change.actions[0].apply(), error => error.stage === 'apply' && error.reason === 'mapper_conflict');
    else {
      await apply(change);
      assert.equal(Object.hasOwn(console.protocolMappers.at(-1).config, 'usermodel.clientRoleMapping.rolePrefix'), false);
      assert.equal((await plan(fixture)).actions.length, 0);
    }
    assert.equal(fixture.writes.length, 1);
  }
});

test('normalization of the intended addition does not weaken raw preservation of an existing mapper', async () => {
  const fixture = provider();
  const mapper = fixture.state.clients.find(client => client.clientId === 'account-console').protocolMappers
    .find(mapper => mapper.protocolMapper === 'oidc-usermodel-client-role-mapper');
  mapper.config['usermodel.clientRoleMapping.rolePrefix'] = '';
  const change = await plan(fixture);
  assert.equal(change.actions.length, 0);
  delete mapper.config['usermodel.clientRoleMapping.rolePrefix'];
  await assert.rejects(change.verify(), error => error.stage === 'readback' && error.reason === 'readback_conflict');
  assert.equal(fixture.writes.length, 0);
});

test('account capability is explicit manifest data and agrees with the exact export grant', async () => {
  for (const variant of ['missing', 'other_role', 'extra_grant', 'default_role']) {
    const fixture = provider();
    const declaration = clone(manifest);
    const exported = clone(desired);
    if (variant === 'missing') delete declaration.users[0].accountCapability;
    if (variant === 'other_role') declaration.users[0].accountCapability.role = 'view-profile';
    if (variant === 'extra_grant') exported.users[0].clientRoles.account.push('view-profile');
    if (variant === 'default_role') exported.users[0].clientRoles = {};
    await assert.rejects(buildRealmPlan({ admin: fixture.admin, desired: exported, manifest: declaration, webSecret: secret }),
      error => error.stage === 'plan' && error.reason === 'identity_conflict');
    assert.equal(fixture.writes.length, 0);
  }
});

test('inherited account capability and a compatible differently named fixed mapper need no new grant or mapper', async () => {
  for (const source of ['group', 'composite']) {
    const fixture = provider();
    const user = fixture.state.users[0];
    const role = fixture.state.accountRoles.find(role => role.name === 'manage-account');
    delete user.roles.clientMappings;
    if (source === 'group') {
      fixture.state.groups.push({ id: 'eligible-group', name: 'Eligible', attributes: {}, clientRoles: { account: ['manage-account'] } });
      fixture.state.memberships[user.id] = ['eligible-group'];
    } else fixture.state.roleChildren[user.roles.realmMappings[0].id] = [role];
    const console = fixture.state.clients.find(client => client.clientId === 'account-console');
    console.protocolMappers.find(mapper => mapper.protocolMapper === 'oidc-usermodel-client-role-mapper').name = 'retained-compatible-name';
    const before = clone(fixture.state);
    const change = await plan(fixture);
    assert.equal(change.actions.length, 0);
    await change.verify();
    assert.deepEqual(fixture.state, before);
  }
});

test('an existing fixed emitter must be effective without selecting an optional console scope', async () => {
  for (const kind of ['defaultClientScopes', 'optionalClientScopes']) {
    const fixture = provider();
    const console = fixture.state.clients.find(client => client.clientId === 'account-console');
    const scope = fixture.state.scopes.find(scope => scope.name === 'phone');
    scope.protocolMappers.push(console.protocolMappers.find(mapper => mapper.protocolMapper === 'oidc-usermodel-client-role-mapper'));
    console.protocolMappers = console.protocolMappers.filter(mapper => mapper.protocolMapper !== 'oidc-usermodel-client-role-mapper');
    console[kind].push(scope.name);
    if (kind === 'optionalClientScopes') await assert.rejects(plan(fixture), error => error.reason === 'unsupported');
    else {
      const change = await plan(fixture);
      assert.equal(change.actions.length, 0);
      await change.verify();
    }
    assert.equal(fixture.writes.length, 0);
  }
});

test('unsafe account composites, direct/default/optional scope allowances and implicit mappers refuse before pending writes', async () => {
  const variants = [
    fixture => { fixture.state.accountRoles.find(role => role.name === 'manage-account').containerId = 'foreign'; },
    fixture => { fixture.state.accountRoles.find(role => role.name === 'manage-account-links').composite = true; },
    fixture => { fixture.state.roleChildren[fixture.state.accountRoles.find(role => role.name === 'manage-account').id].push(fixture.state.roles[0]); },
    fixture => { fixture.state.roleChildren[fixture.state.accountRoles.find(role => role.name === 'manage-account-links').id].push(fixture.state.roles[0]); },
    fixture => { fixture.state.clients.find(client => client.clientId === 'account-console').fullScopeAllowed = true; },
    fixture => { fixture.state.clients.find(client => client.clientId === 'account-console').attributes['pkce.code.challenge.method'] = 'plain'; },
  ];
  for (const location of ['direct', 'default', 'optional']) for (const unsafe of ['realm', 'foreign', 'wider', 'realm-emitter', 'audience', 'transform', 'dynamic-client', 'claim-prefix'])
    variants.push(fixture => {
      const console = fixture.state.clients.find(client => client.clientId === 'account-console');
      const scope = fixture.state.scopes.find(scope => scope.name === 'phone');
      if (location !== 'direct') console[location === 'default' ? 'defaultClientScopes' : 'optionalClientScopes'] = [scope.name];
      const path = location === 'direct' ? `clients/${console.id}` : `client-scopes/${scope.id}`;
      if (['realm', 'foreign', 'wider'].includes(unsafe)) {
        fixture.state.scopeMappings[path] ??= {};
        const mappings = fixture.state.scopeMappings[path];
        if (unsafe === 'realm') mappings.realmMappings = [fixture.state.roles[0]];
        else if (unsafe === 'foreign') mappings.clientMappings = { foreign: { id: 'foreign', client: 'foreign', mappings: [] } };
        else mappings.clientMappings = { account: { id: fixture.state.clients.find(client => client.clientId === 'account').id,
          client: 'account', mappings: [fixture.state.accountRoles.find(role => role.name === 'view-profile')] } };
      } else {
        const mapper = clone(console.protocolMappers.find(mapper => mapper.protocolMapper === 'oidc-usermodel-client-role-mapper'));
        mapper.id = 'unsafe-mapper'; mapper.name = 'retained-unsafe-mapper';
        if (unsafe === 'realm-emitter') mapper.protocolMapper = 'oidc-usermodel-realm-role-mapper';
        if (unsafe === 'audience') { mapper.protocolMapper = 'oidc-audience-mapper'; mapper.config = { 'included.client.audience': 'other' }; }
        if (unsafe === 'transform') { mapper.protocolMapper = 'oidc-hardcoded-role-mapper'; mapper.config = { role: 'account.manage-account' }; }
        if (unsafe === 'dynamic-client') delete mapper.config['usermodel.clientRoleMapping.clientId'];
        if (unsafe === 'claim-prefix') mapper.config['usermodel.clientRoleMapping.rolePrefix'] = 'wider-';
        (location === 'direct' ? console : scope).protocolMappers.push(mapper);
      }
    });
  for (const mutate of variants) {
    const fixture = provider(); fixture.state.realm.accessTokenLifespan = 1800;
    mutate(fixture);
    const original = clone(fixture.state);
    await assert.rejects(plan(fixture), error => error.stage === 'plan'
      && ['unsupported', 'mapper_conflict'].includes(error.reason));
    assert.equal(fixture.writes.length, 0);
    assert.deepEqual(fixture.state, original);
  }
  const clean = provider(); clean.state.realm.accessTokenLifespan = 1800;
  await apply(await plan(clean));
  assert.equal((await plan(clean)).actions.length, 0);
});

test('account additions preserve all prior mappings and mapper IDs; independent readback rejects corruption', async () => {
  for (const tamper of [null, 'mapper_id', 'extra_grant', 'missing_prior_role', 'console_setting', 'effective']) {
    const fixture = provider();
    const user = fixture.state.users[0];
    const console = fixture.state.clients.find(client => client.clientId === 'account-console');
    delete user.roles.clientMappings;
    console.protocolMappers = console.protocolMappers.filter(mapper => mapper.protocolMapper !== 'oidc-usermodel-client-role-mapper');
    const originalMapper = clone(console.protocolMappers[0]);
    const originalRoles = clone(user.roles.realmMappings);
    const change = await plan(fixture);
    assert.deepEqual(change.actions.map(action => action.label), ['create_account_mapper', 'grant_account_self_service']);
    for (const action of change.actions) await action.apply();
    if (tamper === 'mapper_id') console.protocolMappers[0].id = 'replaced';
    if (tamper === 'extra_grant') user.roles.clientMappings.account.mappings.push(fixture.state.accountRoles.find(role => role.name === 'view-profile'));
    if (tamper === 'missing_prior_role') user.roles.realmMappings = [];
    if (tamper === 'console_setting') console.redirectUris = ['https://changed.invalid/*'];
    if (tamper === 'effective') {
      const originalGet = fixture.admin.get;
      fixture.admin.get = async path => path.includes('/composite?') ? [] : originalGet(path);
    }
    if (tamper) await assert.rejects(change.verify(), error => error.stage === 'readback');
    else {
      await change.verify();
      assert.deepEqual(console.protocolMappers[0], originalMapper);
      assert.deepEqual(user.roles.realmMappings, originalRoles);
      assert.equal((await plan(fixture)).actions.length, 0);
      assert.ok(fixture.writes.every(write => write.method === 'POST'));
    }
  }
});

test('account mapper additions preflight response counts and bytes before any earlier pending write', async () => {
  for (const oversized of ['count', 'bytes']) {
    const fixture = provider(); fixture.state.realm.accessTokenLifespan = 1800;
    const console = fixture.state.clients.find(client => client.clientId === 'account-console');
    console.protocolMappers = console.protocolMappers.filter(mapper => mapper.protocolMapper !== 'oidc-usermodel-client-role-mapper');
    const safe = { name: 'retained', id: 'retained', protocol: 'openid-connect', protocolMapper: 'oidc-full-name-mapper', config: {} };
    if (oversized === 'count') for (let index = 0; index < 199; index++) console.protocolMappers.push({ ...safe, id: `safe-${index}`, name: `safe-${index}` });
    else console.protocolMappers.push({ ...safe, retained: 'x'.repeat(256 * 1024) });
    await assert.rejects(plan(fixture), error => error.stage === 'plan'
      && error.reason === (oversized === 'count' ? 'unsupported' : 'body_limit'));
    assert.equal(fixture.writes.length, 0);
  }
});

test('warm realm preflight is read only; merge preserves accounts, credentials, IDs and unrelated state', async () => {
  const fixture = provider(); legacy(fixture);
  const original = clone(fixture.state);
  const change = await plan(fixture);
  assert.ok(change.actions.length > 0);
  assert.equal(fixture.writes.length, 0);
  await apply(change);
  assert.equal((await plan(fixture)).actions.length, 0);
  assert.equal((await plan(fixture)).actions.length, 0);
  assert.equal(fixture.state.users[0].id, original.users[0].id);
  assert.deepEqual(fixture.state.users[0].credentials, original.users[0].credentials);
  assert.deepEqual(omit(fixture.state.users[0].roles, ['clientMappings']), original.users[0].roles);
  assert.deepEqual(fixture.state.users[0].roles.clientMappings.account.mappings.map(role => role.name), ['manage-account']);
  assert.equal(fixture.state.users[0].enabled, false);
  assert.equal(fixture.state.users[0].firstName, 'Retained');
  assert.deepEqual(fixture.state.users[0].requiredActions, ['UPDATE_PASSWORD']);
  assert.deepEqual(fixture.state.users[0].attributes.retained, ['untouched']);
  assert.equal(fixture.state.profile.unmanagedAttributePolicy, 'ADMIN_EDIT');
  assert.deepEqual(fixture.state.profile.groups, original.profile.groups);
  assert.equal(fixture.state.clients.find(client => client.clientId === 'learnstack-api').secret,
    original.clients.find(client => client.clientId === 'learnstack-api').secret);
  for (const write of fixture.writes.filter(write => write.path === `users/${original.users[0].id}`)) {
    assert.deepEqual(Object.keys(write.body).sort(), ['attributes', 'email', 'firstName', 'lastName', 'username']);
  }
});

test('matching upper-case canonical D UUIDs retain their exact representation', async () => {
  const fixture = provider();
  for (const user of fixture.state.users) for (const values of Object.values(user.attributes)) values[0] = values[0].toUpperCase();
  assert.equal((await plan(fixture)).actions.length, 0);
});

test('safe unrelated scopes and mappers survive while owned mapper/profile fields converge', async () => {
  const fixture = provider();
  const safe = { id: 'unrelated-mapper', name: 'unrelated', protocol: 'openid-connect',
    protocolMapper: 'oidc-usermodel-attribute-mapper', config: { 'claim.name': 'retained', 'user.attribute': 'retained' } };
  fixture.state.scopes.push({ id: 'unrelated-scope', name: 'unrelated', protocol: 'openid-connect',
    attributes: { retained: 'value' }, protocolMappers: [clone(safe)] });
  for (const client of fixture.state.clients.filter(client => ['learnstack-web', 'learnstack-api'].includes(client.clientId))) {
    client.defaultClientScopes.push('unrelated');
    client.protocolMappers.push({ ...clone(safe), id: `direct-${client.id}` });
  }
  const owned = fixture.state.scopes.find(scope => scope.name === 'learnstack-tenant-api');
  owned.attributes.retained = 'value';
  owned.protocolMappers[0].config['userinfo.token.claim'] = 'true';
  fixture.state.profile.attributes.find(attribute => attribute.name === 'organization_id').permissions.edit = ['user', 'admin'];
  const change = await plan(fixture);
  assert.deepEqual(change.actions.map(action => action.label), ['update_mapper', 'update_profile']);
  await apply(change);
  assert.equal((await plan(fixture)).actions.length, 0);
  assert.equal(owned.attributes.retained, 'value');
  assert.deepEqual(fixture.state.scopes.at(-1).protocolMappers, [safe]);
});

test('client override deletion requires a tombstone under actual attribute merge semantics', async () => {
  for (const dropTombstone of [false, true]) {
    const fixture = provider();
    for (const client of fixture.state.clients.filter(client => ['learnstack-web', 'learnstack-api'].includes(client.clientId))) {
      client.attributes['access.token.lifespan'] = '900';
      client.attributes.retained = 'untouched';
    }
    const originalPut = fixture.admin.put;
    fixture.admin.put = (path, body) => {
      const sent = clone(body);
      if (dropTombstone && sent?.attributes) delete sent.attributes['access.token.lifespan'];
      return originalPut(path, sent);
    };
    const change = await plan(fixture);
    for (const action of change.actions) await action.apply();
    if (dropTombstone) {
      await assert.rejects(change.verify(), error => error.stage === 'readback');
      assert.equal((await plan(fixture)).actions.length, 2);
    } else {
      await change.verify();
      assert.equal((await plan(fixture)).actions.length, 0);
      for (const client of fixture.state.clients.filter(client => ['learnstack-web', 'learnstack-api'].includes(client.clientId))) {
        assert.equal(Object.hasOwn(client.attributes, 'access.token.lifespan'), false);
        assert.equal(client.attributes.retained, 'untouched');
      }
      assert.ok(fixture.writes.every(write => write.body.attributes['access.token.lifespan'] === null));
    }
  }
});

test('a client PUT preserves existing authorization and avoids reattaching the service account scope', async () => {
  const fixture = provider();
  const api = fixture.state.clients.find(client => client.clientId === 'learnstack-api');
  api.authorizationServicesEnabled = true;
  api.attributes['access.token.lifespan'] = '1800';
  const scopes = clone(api.defaultClientScopes);
  const change = await plan(fixture);
  await apply(change);
  assert.equal(api.authorizationServicesEnabled, true);
  assert.deepEqual(api.defaultClientScopes, scopes);
  const update = fixture.writes.find(write => write.path === `clients/${api.id}`);
  assert.equal(update.body.authorizationServicesEnabled, true);
  assert.equal(Object.hasOwn(update.body, 'serviceAccountsEnabled'), false);
  assert.deepEqual(Object.keys(update.body).sort(), ['attributes', 'authorizationServicesEnabled', 'webOrigins']);
});

test('empty server-only origins remain explicit on client PUT; omission is rejected by independent readback', async () => {
  for (const omitOrigins of [false, true]) {
    const fixture = provider();
    const web = fixture.state.clients.find(client => client.clientId === 'learnstack-web');
    web.attributes['access.token.lifespan'] = '1800';
    const originalPut = fixture.admin.put;
    fixture.admin.put = (path, body) => {
      const sent = clone(body);
      if (omitOrigins && path === `clients/${web.id}`) delete sent.webOrigins;
      return originalPut(path, sent);
    };
    const change = await plan(fixture);
    for (const action of change.actions) await action.apply();
    if (omitOrigins) {
      assert.ok(web.webOrigins.length > 0, 'the provider derives browser origins from the configured redirects');
      await assert.rejects(change.verify(), error => error.stage === 'readback');
    } else {
      await change.verify();
      assert.deepEqual(web.webOrigins, []);
      assert.deepEqual(fixture.writes[0].body.webOrigins, []);
      assert.equal((await plan(fixture)).actions.length, 0);
    }
  }
});

test('service account transitions and orphan cleanup refuse before a pending realm write', async () => {
  for (const variant of ['enable_web', 'disable_api', 'orphan_web']) {
    const fixture = provider(); fixture.state.realm.accessTokenLifespan = 1800;
    const web = fixture.state.clients.find(client => client.clientId === 'learnstack-web');
    const api = fixture.state.clients.find(client => client.clientId === 'learnstack-api');
    if (variant === 'enable_web') web.serviceAccountsEnabled = true;
    else if (variant === 'disable_api') api.serviceAccountsEnabled = false;
    else {
      web.attributes['access.token.lifespan'] = '1800';
      fixture.state.users.push({ id: 'orphan-provider-user', username: 'orphan-provider-user',
        serviceAccountClientId: web.clientId, attributes: {} });
    }
    await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'unsupported');
    assert.equal(fixture.writes.length, 0);
  }
});

test('ordinary secret creation time is preserved; a needed client PUT refuses configured rotation state up front', async () => {
  const ordinary = provider();
  const api = ordinary.state.clients.find(client => client.clientId === 'learnstack-api');
  api.attributes['client.secret.creation.time'] = '1700000000';
  api.attributes['access.token.lifespan'] = '1800';
  await apply(await plan(ordinary));
  assert.equal(api.attributes['client.secret.creation.time'], '1700000000');
  assert.equal((await plan(ordinary)).actions.length, 0);
  for (const attribute of ['client.secret.expiration.time', 'client.secret.remaining.expiration.time',
    'client.secret.rotated', 'client.secret.rotated.creation.time', 'client.secret.rotated.expiration.time']) {
    const fixture = provider();
    const current = fixture.state.clients.find(client => client.clientId === 'learnstack-api');
    current.attributes[attribute] = '1900000000';
    assert.equal((await plan(fixture)).actions.length, 0, 'no-op reads preserve existing rotation configuration');
    fixture.state.realm.accessTokenLifespan = 1800;
    current.attributes['access.token.lifespan'] = '1800';
    await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'unsupported');
    assert.equal(fixture.writes.length, 0);
    assert.equal(current.attributes[attribute], '1900000000');
  }
});

test('identity, mapper and secret conflicts all refuse before any write', async () => {
  const mutations = [
    [fixture => { fixture.state.users[0].attributes.user_id = ['malformed']; }, 'identity_conflict'],
    [fixture => { fixture.state.users[0].attributes.user_id = [`{${manifest.users[0].userId}}`]; }, 'identity_conflict'],
    [fixture => { fixture.state.users[0].attributes.tenant_id.push(manifest.users[0].tenantId); }, 'identity_conflict'],
    [fixture => { fixture.state.users[0].attributes.organization_id = [manifest.users[2].organizationId]; }, 'identity_conflict'],
    [fixture => { fixture.state.users.push({ ...clone(fixture.state.users[0]), id: 'duplicate-account' }); }, 'identity_conflict'],
    [fixture => { fixture.state.users.push({ id: 'collision', username: 'unrelated', attributes: { user_id: [manifest.users[0].userId] } }); }, 'identity_conflict'],
    [fixture => { fixture.state.clients[0].protocolMappers.push({ ...clone(desired.clientScopes.find(scope => scope.name === 'learnstack-tenant-api').protocolMappers[0]), id: 'conflicting-direct' }); }, 'mapper_conflict'],
    [fixture => { fixture.state.clients[0].protocolMappers.push({ id: 'unknown', name: 'unknown', protocol: 'openid-connect', protocolMapper: 'unqualified' }); }, 'mapper_conflict'],
    [fixture => { fixture.state.clients.find(client => client.clientId === 'learnstack-web').secret = 'different-contract'; }, 'secret_conflict'],
    [fixture => { fixture.state.users.pop(); fixture.state.roles.find(role => role.name === manifest.users.at(-1).realmRole).composite = true; }, 'unsupported'],
  ];
  for (const [index, [mutate, reason]] of mutations.entries()) {
    const fixture = provider(); mutate(fixture);
    await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === reason, `mutation ${index}`);
    assert.equal(fixture.writes.length, 0);
  }
});

test('census and exact username disagreement refuse before a pending owned write', async () => {
  for (const inconsistent of ['projected_username', 'missing_exact', 'different_exact_id']) {
    const fixture = provider(); fixture.state.realm.accessTokenLifespan = 1800;
    const originalGet = fixture.admin.get;
    fixture.admin.get = async path => {
      const result = await originalGet(path);
      const exact = path.startsWith('users?username=')
        && new URL(path, 'http://provider/').searchParams.get('username') === manifest.users[0].username;
      if (inconsistent === 'projected_username'
        && (path.startsWith('users?first=') || exact)) {
        for (const user of result.filter(value => value.id === fixture.state.users[0].id)) user.username = 'changed-profile-projection';
      }
      if (exact && inconsistent === 'missing_exact') return [];
      if (exact && inconsistent === 'different_exact_id') result[0].id = 'disagreeing-provider-id';
      return result;
    };
    await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'identity_conflict');
    assert.equal(fixture.writes.length, 0);
    assert.equal(fixture.state.realm.accessTokenLifespan, 1800);
  }
});

test('hidden or synthesized protected values refuse before writes, including the all-user actor census', async () => {
  for (const variant of ['unmanaged_disabled', 'managed_hidden', 'default_value', 'selector', 'normalizer', 'hidden_duplicate_actor']) {
    const fixture = provider(); legacy(fixture);
    fixture.state.profile.unmanagedAttributePolicy = null;
    fixture.state.users[0].attributes.user_id = [manifest.users[1].userId];
    if (variant !== 'unmanaged_disabled' && variant !== 'hidden_duplicate_actor') {
      const definition = clone(provider().state.profile.attributes.find(attribute => attribute.name === 'user_id'));
      fixture.state.profile.attributes.push(...clone(provider().state.profile.attributes.filter(attribute =>
        ['tenant_id', 'organization_id'].includes(attribute.name))));
      if (variant === 'managed_hidden') definition.permissions = { view: ['user'], edit: ['user'] };
      if (variant === 'default_value') definition.defaultValue = manifest.users[0].userId;
      if (variant === 'selector') definition.selector = { scopes: ['profile'] };
      if (variant === 'normalizer') definition.normalizer = 'unqualified';
      fixture.state.profile.attributes.push(definition);
    }
    if (variant === 'hidden_duplicate_actor') {
      delete fixture.state.users[0].attributes.user_id;
      fixture.state.users.at(-1).attributes.user_id = [manifest.users[0].userId];
    }
    const original = clone(fixture.state);
    await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'identity_conflict', variant);
    assert.equal(fixture.writes.length, 0);
    assert.deepEqual(fixture.state, original);
    assert.deepEqual(fixture.reads, ['', 'users/profile'], 'profile qualification precedes projected account reads');
  }
});

test('managed admin-readable values and explicit unmanaged admin policies remain supported', async () => {
  for (const policy of ['ENABLED', 'ADMIN_EDIT', 'ADMIN_VIEW']) {
    const fixture = provider(); legacy(fixture);
    fixture.state.profile.unmanagedAttributePolicy = policy;
    await apply(await plan(fixture));
    assert.equal(fixture.state.profile.unmanagedAttributePolicy, policy);
    assert.equal((await plan(fixture)).actions.length, 0);
  }
  const editOnly = provider();
  for (const definition of editOnly.state.profile.attributes.filter(attribute =>
    ['user_id', 'tenant_id', 'organization_id'].includes(attribute.name))) definition.permissions.view = [];
  await apply(await plan(editOnly));
  assert.equal((await plan(editOnly)).actions.length, 0, 'pinned admin edit permission also grants view');
});

test('the all-user actor census refuses unqualified external-storage profile metadata before writes', async () => {
  for (const external of [{ id: 'retained-external', federationLink: 'retained-provider' }, { id: 'f:retained-provider:retained-external' }]) {
    const fixture = provider(); fixture.state.realm.accessTokenLifespan = 1800;
    fixture.state.users.push({ ...external, username: 'retained-external', attributes: {} });
    const original = clone(fixture.state);
    await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'identity_conflict');
    assert.equal(fixture.writes.length, 0);
    assert.deepEqual(fixture.state, original);
  }
});

test('existing attribute updates reject unrelated writable projection defaults and retain hidden readonly values', async () => {
  for (const projection of [{ defaultValue: 'synthesized' }, { selector: { scopes: ['profile'] } }, { normalizer: 'unqualified' }]) {
    const fixture = provider();
    fixture.state.realm.accessTokenLifespan = 1800;
    delete fixture.state.users[0].attributes.tenant_id;
    fixture.state.profile.attributes.push({ name: 'unrelated', permissions: { view: ['admin'], edit: ['admin'] }, ...projection });
    const original = clone(fixture.state);
    await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'unsupported');
    assert.equal(fixture.writes.length, 0);
    assert.deepEqual(fixture.state, original);
  }
  const readonly = provider();
  delete readonly.state.users[0].attributes.tenant_id;
  readonly.state.users[0].attributes.unmanaged_hidden = ['retained'];
  readonly.state.users[0].attributes.managed_hidden = ['retained'];
  readonly.state.profile.attributes.push({ name: 'managed_hidden', permissions: { view: ['user'], edit: ['user'] } });
  await apply(await plan(readonly));
  assert.deepEqual(readonly.state.users[0].attributes.unmanaged_hidden, ['retained']);
  assert.deepEqual(readonly.state.users[0].attributes.managed_hidden, ['retained']);
  assert.equal(readonly.state.profile.unmanagedAttributePolicy, undefined);
  const creating = provider();
  creating.state.realm.accessTokenLifespan = 1800;
  creating.state.users.pop();
  creating.state.profile.attributes.push({ name: 'unrelated_default', defaultValue: 'synthesized',
    permissions: { view: ['admin'], edit: [] } });
  const original = clone(creating.state);
  await assert.rejects(plan(creating), error => error.stage === 'plan' && error.reason === 'unsupported');
  assert.equal(creating.writes.length, 0, 'a new account must not acquire an unqualified readback projection after creation');
  assert.deepEqual(creating.state, original);
});

function fillScopes(fixture, count) {
  while (fixture.state.scopes.length < count) {
    const index = fixture.state.scopes.length;
    fixture.state.scopes.push({ id: `retained-scope-${index}`, name: `retained-scope-${index}`,
      protocol: 'openid-connect', attributes: {}, protocolMappers: [] });
  }
}

test('projected scopes, profile definitions and mapper counts stay within the readback bound', async () => {
  for (const resource of ['scope', 'profile', 'mapper']) for (const total of [200, 201]) {
    const fixture = provider();
    fixture.state.realm.accessTokenLifespan = 1800;
    if (resource === 'scope') {
      fixture.state.scopes = fixture.state.scopes.filter(scope => scope.name !== 'learnstack-tenant-api');
      for (const client of fixture.state.clients)
        client.defaultClientScopes = client.defaultClientScopes.filter(name => name !== 'learnstack-tenant-api');
      fillScopes(fixture, total - 1);
    } else if (resource === 'profile') {
      fixture.state.profile.unmanagedAttributePolicy = 'ADMIN_EDIT';
      fixture.state.profile.attributes = fixture.state.profile.attributes.filter(attribute =>
        !['user_id', 'tenant_id', 'organization_id'].includes(attribute.name));
      while (fixture.state.profile.attributes.length < total - 3)
        fixture.state.profile.attributes.push({ name: `retained-${fixture.state.profile.attributes.length}`,
          permissions: { view: ['admin'], edit: ['admin'] } });
    } else {
      const scope = fixture.state.scopes.find(value => value.name === 'basic');
      scope.protocolMappers = scope.protocolMappers.filter(mapper => mapper.protocolMapper !== 'oidc-sub-mapper');
      while (scope.protocolMappers.length < total - 1) {
        const index = scope.protocolMappers.length;
        scope.protocolMappers.push({ id: `retained-mapper-${index}`, name: `retained-mapper-${index}`,
          protocol: 'openid-connect', protocolMapper: 'oidc-usermodel-attribute-mapper',
          config: { 'claim.name': `retained_${index}`, 'user.attribute': `retained_${index}` } });
      }
    }
    const original = clone(fixture.state);
    if (total === 201) {
      await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'unsupported', resource);
      assert.equal(fixture.writes.length, 0);
      assert.deepEqual(fixture.state, original);
    } else {
      await apply(await plan(fixture));
      assert.equal((await plan(fixture)).actions.length, 0, resource);
    }
  }
});

test('assignment and profile-group exact boundaries remain supported and excess groups refuse before writes', async () => {
  const assigned = provider();
  const web = assigned.state.clients.find(client => client.clientId === 'learnstack-web');
  assigned.state.scopes = assigned.state.scopes.filter(scope => scope.protocol === 'openid-connect');
  fillScopes(assigned, 200);
  web.defaultClientScopes = assigned.state.scopes.filter(scope => scope.name !== 'learnstack-tenant-api').map(scope => scope.name);
  web.optionalClientScopes = [];
  await apply(await plan(assigned));
  assert.equal(web.defaultClientScopes.length, 200);
  assert.equal((await plan(assigned)).actions.length, 0);
  for (const count of [200, 201]) {
    const fixture = provider(); fixture.state.realm.accessTokenLifespan = 1800;
    fixture.state.profile.groups = Array.from({ length: count }, (_, index) => ({ name: `retained-group-${index}` }));
    if (count === 200) await apply(await plan(fixture));
    else {
      await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'unsupported');
      assert.equal(fixture.writes.length, 0);
    }
  }
});

test('projected accounts allow 999 with readable full metadata pages and refuse 1000 before earlier writes', async () => {
  for (const total of [999, 1000]) {
    const fixture = provider();
    fixture.state.realm.accessTokenLifespan = 1800;
    fixture.state.users = fixture.state.users.slice(0, 2);
    while (fixture.state.users.length < total - 2) {
      const index = fixture.state.users.length;
      fixture.state.users.push({ id: `retained-user-${index}`, username: `retained-user-${index}`, attributes: {} });
    }
    const originalGet = fixture.admin.get;
    fixture.admin.get = async path => {
      const value = await originalGet(path);
      if (path.startsWith('users?')) for (const user of value) user.userProfileMetadata = {
        attributes: fixture.state.profile.attributes.map(attribute => ({ name: attribute.name,
          displayName: attribute.displayName, required: false, readOnly: false, multivalued: false,
          annotations: attribute.annotations ?? {}, validators: Object.fromEntries(Object.entries(attribute.validations ?? {})
            .map(([name, config]) => [name, { ...config, 'ignore.empty.value': true }])) })),
        groups: clone(fixture.state.profile.groups),
      };
      return value;
    };
    capResponses(fixture);
    const original = clone(fixture.state);
    if (total === 1000) {
      await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'unsupported');
      assert.equal(fixture.writes.length, 0);
      assert.deepEqual(fixture.state, original);
    } else {
      await apply(await plan(fixture));
      assert.equal(fixture.state.users.length, 999);
      assert.equal((await plan(fixture)).actions.length, 0);
    }
  }
});

test('UTF-8 mutation bytes and repeated response metadata are both qualified before returning actions', async () => {
  for (const desiredBodyBytes of [16 * 1024, 256 * 1024, 256 * 1024 + 1]) {
    const fixture = provider();
    fixture.state.scopes = fixture.state.scopes.filter(scope => scope.name !== 'learnstack-tenant-api');
    for (const client of fixture.state.clients)
      client.defaultClientScopes = client.defaultClientScopes.filter(name => name !== 'learnstack-tenant-api');
    const changed = fixture.state.profile.attributes.find(attribute => attribute.name === 'user_id');
    changed.permissions.edit = ['user'];
    changed.annotations = { retained: '' };
    const merged = clone(fixture.state.profile);
    merged.attributes.find(attribute => attribute.name === 'user_id').permissions.edit = ['admin'];
    const paddingBytes = desiredBodyBytes - Buffer.byteLength(JSON.stringify(merged), 'utf8');
    changed.annotations.retained = 'é'.repeat(Math.floor(paddingBytes / 2)) + 'x'.repeat(paddingBytes % 2);
    const original = clone(fixture.state);
    if (desiredBodyBytes > 16 * 1024) {
      await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'body_limit');
      assert.equal(fixture.writes.length, 0);
      assert.deepEqual(fixture.state, original);
    } else {
      await apply(await plan(fixture));
      const write = fixture.writes.find(value => value.path === 'users/profile');
      assert.equal(Buffer.byteLength(JSON.stringify(write.body), 'utf8'), desiredBodyBytes);
      assert.equal((await plan(fixture)).actions.length, 0);
    }
  }
});

function capResponses(fixture) {
  const originalGet = fixture.admin.get;
  fixture.admin.get = async path => {
    const value = await originalGet(path);
    assert.ok(Buffer.byteLength(JSON.stringify(value), 'utf8') <= 256 * 1024, 'fixture response exceeds the real transport limit');
    return value;
  };
}

test('known projected user pages and scope inventories must fit the response cap before small valid mutations', async () => {
  for (const resource of ['users', 'scopes']) for (const supported of [true, false]) {
    const fixture = provider();
    const headroom = supported ? 16 * 1024 : 16;
    const path = resource === 'users' ? 'users?first=0&max=100&briefRepresentation=false&exact=false' : 'client-scopes';
    if (resource === 'users') {
      fixture.state.profile.unmanagedAttributePolicy = 'ADMIN_EDIT';
      fixture.state.users[0].attributes = { user_id: [manifest.users[0].userId] };
      let remaining = 256 * 1024 - headroom - Buffer.byteLength(JSON.stringify(await fixture.admin.get(path)), 'utf8');
      for (let index = 0; remaining > 0; index += 1) {
        const name = `retained_${index}`;
        // Keep individual values within the pinned default attribute length.
        const punctuation = Buffer.byteLength(JSON.stringify({ [name]: [''] }), 'utf8') - 1;
        const length = Math.min(2000, Math.max(0, remaining - punctuation));
        fixture.state.users[0].attributes[name] = ['x'.repeat(length)];
        remaining -= punctuation + length;
      }
    } else {
      fixture.state.scopes = fixture.state.scopes.filter(scope => scope.name !== 'learnstack-tenant-api');
      for (const client of fixture.state.clients)
        client.defaultClientScopes = client.defaultClientScopes.filter(name => name !== 'learnstack-tenant-api');
      fixture.state.scopes.push({ id: 'retained-large-scope', name: 'retained-large-scope', protocol: 'openid-connect',
        attributes: { retained: '' }, protocolMappers: [] });
      fixture.state.scopes.at(-1).attributes.retained = 'x'.repeat(256 * 1024 - headroom
        - Buffer.byteLength(JSON.stringify(await fixture.admin.get(path)), 'utf8'));
    }
    const original = clone(fixture.state);
    capResponses(fixture);
    if (supported) {
      await apply(await plan(fixture));
      assert.equal((await plan(fixture)).actions.length, 0);
    } else {
      await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'body_limit', resource);
      assert.equal(fixture.writes.length, 0);
      assert.deepEqual(fixture.state, original);
    }
  }
});

test('projected profile metadata growth is budgeted in every full user-list record', async () => {
  const fixture = provider();
  fixture.state.profile.unmanagedAttributePolicy = 'ADMIN_EDIT';
  fixture.state.profile.attributes = fixture.state.profile.attributes.filter(attribute =>
    !['user_id', 'tenant_id', 'organization_id'].includes(attribute.name));
  const originalGet = fixture.admin.get;
  let padding = '';
  fixture.admin.get = async path => {
    const value = await originalGet(path);
    if (path.startsWith('users?')) for (const user of value) user.userProfileMetadata = {
      attributes: fixture.state.profile.attributes.map(attribute => ({ name: attribute.name,
        displayName: attribute.displayName, required: false, readOnly: false, multivalued: false })),
      groups: [], retained: padding,
    };
    return value;
  };
  const initialBytes = Buffer.byteLength(JSON.stringify(await fixture.admin.get('users?first=0&max=100&briefRepresentation=false&exact=false')), 'utf8');
  padding = 'x'.repeat(Math.floor((256 * 1024 - 128 - initialBytes) / fixture.state.users.length));
  const original = clone(fixture.state);
  capResponses(fixture);
  await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'body_limit');
  assert.equal(fixture.writes.length, 0);
  assert.deepEqual(fixture.state, original);
});

test('new-user response estimates include profile metadata even without an existing ordinary-user template', async () => {
  for (const largeMetadata of [false, true]) {
    const fixture = provider();
    fixture.state.users = [];
    fixture.state.profile.attributes[0].annotations = { retained: 'x'.repeat(largeMetadata ? 70 * 1024 : 0) };
    const original = clone(fixture.state);
    capResponses(fixture);
    if (largeMetadata) {
      await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'body_limit');
      assert.equal(fixture.writes.length, 0);
      assert.deepEqual(fixture.state, original);
    } else {
      await apply(await plan(fixture));
      assert.equal(fixture.state.users.length, manifest.users.length);
      assert.ok(fixture.state.users.every(user => user.id.startsWith('provider-')), 'budget UUIDs never become stored provider IDs');
    }
  }
});

test('partial success is durable and an explicit next run converges', async () => {
  const fixture = provider(); legacy(fixture);
  const change = await plan(fixture);
  await change.actions[0].apply();
  const resumed = await plan(fixture);
  assert.ok(resumed.actions.length < change.actions.length);
  await apply(resumed);
  assert.equal((await plan(fixture)).actions.length, 0);
});

test('one named-user import atomically carries credential and inert role; lost response resumes without role mutation', async () => {
  const fixture = provider(); fixture.state.users.pop();
  const originalPost = fixture.admin.post;
  fixture.admin.post = async (path, body) => {
    const response = await originalPost(path, body);
    if (path === 'partialImport') throw new Error('lost response');
    return response;
  };
  const change = await plan(fixture);
  assert.deepEqual(change.actions.map(action => action.label), ['create_user']);
  await assert.rejects(change.actions[0].apply());
  const write = fixture.writes[0];
  assert.equal(write.path, 'partialImport');
  assert.equal(write.body.users[0].credentials[0].type, 'password');
  assert.deepEqual(write.body.users[0].realmRoles, [manifest.users.at(-1).realmRole]);
  assert.deepEqual(write.body.users[0].clientRoles, { account: ['manage-account'] });
  assert.equal(Object.hasOwn(await fixture.admin.get(`users/${fixture.state.users.at(-1).id}`), 'clientRoles'), false);
  assert.equal((await plan(fixture)).actions.length, 0);
  assert.equal(fixture.writes.length, 1);
});

test('creation409 refuses an unproven raced credential without fresh-account adoption or retry', async () => {
  for (const compatible of [true, false]) {
    const fixture = provider(); fixture.state.users.pop();
    const originalPost = fixture.admin.post;
    fixture.admin.post = async (path, body) => {
      await originalPost(path, body);
      fixture.state.users.at(-1).credentials[0].credentialData = 'different-raced-password';
      if (!compatible) fixture.state.users.at(-1).roles.realmMappings = [];
      return { status: 409 };
    };
    const change = await plan(fixture);
    const initialLookups = fixture.reads.filter(path => path.includes('username=')).length;
    await assert.rejects(change.actions[0].apply(), error => error.stage === 'apply' && error.reason === 'identity_conflict');
    assert.equal(fixture.writes.length, 1);
    assert.equal(fixture.reads.filter(path => path.includes('username=')).length - initialLookups, 0);
    assert.equal(fixture.writes[0].body.ifResourceExists, 'FAIL');
    assert.equal(fixture.state.users.at(-1).credentials[0].credentialData, 'different-raced-password');
  }
});

test('scope and mapper creation409 accept only exact fresh resources without retry', async () => {
  for (const resource of ['scope', 'mapper']) {
    const fixture = provider();
    const scope = fixture.state.scopes.find(value => value.name === 'basic');
    if (resource === 'scope') {
      fixture.state.scopes = fixture.state.scopes.filter(value => value.name !== 'learnstack-tenant-api');
      for (const client of fixture.state.clients) client.defaultClientScopes = client.defaultClientScopes.filter(name => name !== 'learnstack-tenant-api');
    } else scope.protocolMappers = scope.protocolMappers.filter(mapper => mapper.protocolMapper !== 'oidc-sub-mapper');
    const originalPost = fixture.admin.post;
    fixture.admin.post = async (path, body) => { await originalPost(path, body); return { status: 409 }; };
    const change = await plan(fixture);
    await apply(change);
    assert.equal(fixture.writes.filter(write => write.method === 'POST').length, 1);
    assert.equal((await plan(fixture)).actions.length, 0);
  }
});

test('unbounded user pages refuse during read-only preflight', async () => {
  const paged = provider();
  const originalGet = paged.admin.get;
  let calls = 0;
  paged.admin.get = path => path.startsWith('users?first=') ? Promise.resolve(Array.from({ length: 100 }, (_, index) => ({
    id: `page-${calls}-${index}`, username: `page-${calls}-${index}`,
  }))).finally(() => { calls += 1; }) : originalGet(path);
  await assert.rejects(plan(paged), error => error.stage === 'plan' && error.reason === 'unsupported');
  assert.equal(calls, 10);
  assert.equal(paged.writes.length, 0);
});

test('readback detects preserved field and credential corruption', async () => {
  for (const tamper of [
    fixture => { fixture.state.users[0].firstName = 'changed'; },
    fixture => { fixture.state.users[0].credentials[0].id = 'changed'; },
    fixture => { fixture.state.realm.displayName = 'changed'; },
    fixture => { fixture.state.profile.groups = []; },
  ]) {
    const fixture = provider();
    const change = await plan(fixture); tamper(fixture);
    await assert.rejects(change.verify(), error => error.stage === 'readback');
  }
});

test('pinned claim-path parsing refuses escaped/dynamic protected roots but keeps literal dotted keys and safe nested client roles', () => {
  const mappers = desired.clientScopes.filter(scope => ['basic', 'roles', 'learnstack-tenant-api'].includes(scope.name)).flatMap(scope => scope.protocolMappers);
  const identity = desired.clientScopes.find(scope => scope.name === 'learnstack-tenant-api').protocolMappers[0];
  for (const destination of ['user_id.nested', 'user\\_id', 'tenant\\_id.nested', 'sub', 'aud']) {
    const mapper = { ...clone(identity), name: 'extra', config: { ...identity.config, 'claim.name': destination } };
    assert.throws(() => validateEffectiveMappers([...mappers, mapper]), error => error.reason === 'mapper_conflict');
  }
  assert.equal(claimRoot('user_id\\.nested'), 'user_id.nested');
  validateEffectiveMappers([...mappers, { ...clone(identity), name: 'literal', config: { ...identity.config, 'claim.name': 'user_id\\.nested' } }]);
  const clientRole = clone(mappers.find(mapper => mapper.protocolMapper === 'oidc-usermodel-client-role-mapper'));
  clientRole.config['claim.name'] = '${client_id}';
  assert.throws(() => validateEffectiveMappers([...mappers, clientRole]));
  clientRole.config['claim.name'] = 'resource_access.${client_id}.roles';
  validateEffectiveMappers([...mappers, clientRole]);
});

test('protected attribute aggregation refuses before group inspection, including a desired config overlay', async () => {
  for (const aggregate of ['true', 'TRUE', true, null, 'unqualified']) {
    const fixture = provider(); fixture.state.realm.accessTokenLifespan = 1800;
    const mapper = fixture.state.scopes.find(scope => scope.name === 'learnstack-tenant-api').protocolMappers[0];
    mapper.config['aggregate.attrs'] = aggregate;
    const original = clone(fixture.state);
    await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'mapper_conflict');
    assert.equal(fixture.writes.length, 0);
    assert.equal(fixture.reads.some(path => path.startsWith('groups')), false);
    assert.deepEqual(fixture.state, original);
  }
  const fixture = provider();
  fixture.state.scopes.find(scope => scope.name === 'learnstack-tenant-api').protocolMappers[0].config['aggregate.attrs'] = 'true';
  const declared = clone(desired);
  declared.clientScopes.find(scope => scope.name === 'learnstack-tenant-api').protocolMappers[0].config['aggregate.attrs'] = 'false';
  await assert.rejects(buildRealmPlan({ admin: fixture.admin, desired: declared, manifest: clone(manifest), webSecret: secret }),
    error => error.stage === 'plan' && error.reason === 'mapper_conflict');
  assert.equal(fixture.writes.length, 0);
  const safe = provider();
  for (const mapper of safe.state.scopes.find(scope => scope.name === 'learnstack-tenant-api').protocolMappers)
    mapper.config['aggregate.attrs'] = 'false';
  assert.equal((await plan(safe)).actions.length, 0);
});

function safeGroupHierarchy(fixture) {
  fixture.state.groups = [
    { id: 'retained-parent', name: 'Retained parent', path: '/retained-parent', attributes: { retained: ['parent value'] }, realmRoles: ['retained-role'] },
    { id: 'retained-child', name: 'Retained child', parentId: 'retained-parent', path: '/retained-parent/retained-child',
      attributes: { retained: ['child value'], organization_id: [] } },
  ];
  fixture.state.memberships[fixture.state.users[0].id] = ['retained-child'];
}

test('safe nested group attributes, roles and membership IDs survive warm reconciliation', async () => {
  const fixture = provider(); legacy(fixture); safeGroupHierarchy(fixture);
  const groups = clone(fixture.state.groups);
  const memberships = clone(fixture.state.memberships);
  await apply(await plan(fixture));
  assert.deepEqual(fixture.state.groups, groups);
  assert.deepEqual(fixture.state.memberships, memberships);
  assert.equal((await plan(fixture)).actions.length, 0);
  assert.ok(fixture.writes.every(write => !write.path.includes('/groups') && !write.path.startsWith('groups')));
  const groupReads = fixture.reads.filter(path => path.includes('/groups?'));
  assert.ok(groupReads.length > 0);
  for (const path of groupReads) {
    const query = new URL(path, 'http://provider/').searchParams;
    assert.equal(query.get('briefRepresentation'), 'true');
    assert.equal(query.has('first') || query.has('max') || query.has('search'), false);
  }
});

test('protected group or ancestor values cannot supply claims when scalar user attributes are absent', async () => {
  for (const claim of ['user_id', 'tenant_id', 'organization_id']) for (const value of [['inherited'], [''], 'malformed', null]) {
    const fixture = provider(); fixture.state.realm.accessTokenLifespan = 1800; safeGroupHierarchy(fixture);
    fixture.state.groups[0].attributes[claim] = value;
    fixture.state.users.push({ id: 'retained-no-actor', username: 'retained-no-actor', attributes: {} });
    fixture.state.memberships['retained-no-actor'] = ['retained-child'];
    const original = clone(fixture.state);
    await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'identity_conflict');
    assert.equal(fixture.writes.length, 0);
    assert.deepEqual(fixture.state, original);
  }
});

test('a provider-exposed nonordinary membership refuses while pagination would conceal it', async () => {
  const fixture = provider(); fixture.state.realm.accessTokenLifespan = 1800;
  fixture.state.realm.organizationsEnabled = false;
  fixture.state.groups.push({ id: 'retained-organization-group', name: 'Retained organization', type: 'organization', attributes: {} });
  fixture.state.memberships[fixture.state.users[0].id] = ['retained-organization-group'];
  const base = `users/${fixture.state.users[0].id}/groups?briefRepresentation=true`;
  assert.equal((await fixture.admin.get(`${base}&first=0&max=201`)).length, 0, 'the pinned paginated branch excludes organization groups');
  assert.equal((await fixture.admin.get(base)).length, 1, 'the mapper and intentional null/null lookup see retained organization groups');
  const original = clone(fixture.state);
  await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'unsupported');
  assert.equal(fixture.writes.length, 0);
  assert.deepEqual(fixture.state, original);
});

test('encoded-space group search includes descendants that REST empty-search injection omits', async () => {
  const fixture = provider(); safeGroupHierarchy(fixture);
  const parameters = '&exact=false&first=0&max=201&briefRepresentation=false&populateHierarchy=false&subGroupsCount=false';
  assert.equal((await fixture.admin.get('groups/count')).count, 2);
  const incomplete = await fixture.admin.get(`groups?search=${parameters}`);
  assert.equal(incomplete.length, 1);
  assert.equal(incomplete[0].parentId, undefined);
  const complete = await fixture.admin.get(`groups?search=%20${parameters}`);
  assert.equal(complete.length, 2);
  assert.ok(complete.some(group => group.parentId === complete[0].id));
  const change = await plan(fixture);
  assert.equal(change.actions.length, 0);
  await change.verify();
  assert.equal(fixture.reads.filter(path => path.startsWith('groups?')).at(-1), `groups?search=%20${parameters}`);
});

test('group inventory completeness, parent closure and membership bounds qualify before pending writes', async () => {
  for (const variant of ['duplicate_id', 'missing_parent', 'cycle', 'truncated', 'too_many_groups', 'too_many_memberships']) {
    const fixture = provider(); fixture.state.realm.accessTokenLifespan = 1800; safeGroupHierarchy(fixture);
    if (variant === 'duplicate_id') fixture.state.groups.push(clone(fixture.state.groups[0]));
    if (variant === 'missing_parent') fixture.state.groups[0].parentId = 'unknown-parent';
    if (variant === 'cycle') fixture.state.groups[0].parentId = 'retained-child';
    if (variant === 'too_many_groups') fixture.state.groups = Array.from({ length: 201 }, (_, index) => ({ id: `retained-${index}`, name: `Retained ${index}`, attributes: {} }));
    if (variant === 'truncated') {
      const originalGet = fixture.admin.get;
      fixture.admin.get = async path => {
        const result = await originalGet(path);
        return path.startsWith('groups?') ? result.slice(0, -1) : result;
      };
    }
    if (variant === 'too_many_memberships') {
      const originalGet = fixture.admin.get;
      fixture.admin.get = async path => path.includes('/groups?')
        ? Array.from({ length: 201 }, (_, index) => ({ id: `membership-${index}` })) : originalGet(path);
    }
    const original = clone(fixture.state);
    await assert.rejects(plan(fixture), error => error.stage === 'plan' && error.reason === 'unsupported', variant);
    assert.equal(fixture.writes.length, 0);
    assert.deepEqual(fixture.state, original);
  }
  const boundary = provider();
  boundary.state.groups = Array.from({ length: 200 }, (_, index) => ({ id: `retained-${index}`, name: `Retained ${index}`, attributes: {} }));
  boundary.state.memberships[boundary.state.users[0].id] = boundary.state.groups.map(group => group.id);
  const change = await plan(boundary);
  assert.equal(change.actions.length, 0);
  await change.verify();
});

test('independent readback catches group changes, membership changes and an unexpected new-account membership', async () => {
  for (const variant of ['group_attribute', 'existing_membership', 'new_membership']) {
    const fixture = provider(); safeGroupHierarchy(fixture);
    if (variant === 'new_membership') fixture.state.users.pop();
    const change = await plan(fixture);
    for (const action of change.actions) await action.apply();
    if (variant === 'group_attribute') fixture.state.groups[0].attributes.retained = ['changed'];
    if (variant === 'existing_membership') fixture.state.memberships[fixture.state.users[0].id] = ['retained-parent'];
    if (variant === 'new_membership') fixture.state.memberships[fixture.state.users.at(-1).id] = ['retained-parent'];
    await assert.rejects(change.verify(), error => error.stage === 'readback' && error.reason === 'readback_conflict');
  }
});

test('a created or raced account with group membership refuses its exact fresh check without changing that membership', async () => {
  for (const status of [200, 409]) {
    const fixture = provider(); safeGroupHierarchy(fixture); fixture.state.users.pop();
    const originalPost = fixture.admin.post;
    fixture.admin.post = async (path, body) => {
      await originalPost(path, body);
      fixture.state.memberships[fixture.state.users.at(-1).id] = ['retained-parent'];
      return { status };
    };
    const change = await plan(fixture);
    assert.deepEqual(change.actions.map(action => action.label), ['create_user']);
    await assert.rejects(change.actions[0].apply(), error => error.stage === 'apply' && error.reason === 'identity_conflict');
    assert.equal(fixture.writes.length, 1);
    assert.deepEqual(fixture.state.memberships[fixture.state.users.at(-1).id], ['retained-parent']);
  }
});

test('profile-derived metadata may change for unrelated accounts while every stored field remains checked', async () => {
  for (const tamper of [null, 'firstName', 'enabled', 'attributes']) {
    const fixture = provider();
    fixture.state.profile.unmanagedAttributePolicy = 'ADMIN_EDIT';
    fixture.state.profile.attributes.find(attribute => attribute.name === 'organization_id').permissions.edit = ['user'];
    fixture.state.users.push({ id: 'retained-account', username: 'retained-account', enabled: false, firstName: 'Retained', attributes: { retained: ['value'] } });
    const before = clone(fixture.state.users.at(-1));
    const beforeList = await fixture.admin.get('users?max=100&briefRepresentation=false');
    const change = await plan(fixture);
    assert.deepEqual(change.actions.map(action => action.label), ['update_profile']);
    for (const action of change.actions) await action.apply();
    const afterList = await fixture.admin.get('users?max=100&briefRepresentation=false');
    assert.notDeepEqual(afterList.at(-1).userProfileMetadata, beforeList.at(-1).userProfileMetadata);
    if (tamper === 'firstName') fixture.state.users.at(-1).firstName = 'Changed';
    if (tamper === 'enabled') fixture.state.users.at(-1).enabled = true;
    if (tamper === 'attributes') fixture.state.users.at(-1).attributes.retained = ['changed'];
    if (tamper) await assert.rejects(change.verify(), error => error.stage === 'readback' && error.reason === 'readback_conflict');
    else {
      await change.verify();
      assert.deepEqual(fixture.state.users.at(-1), before);
    }
  }
});
