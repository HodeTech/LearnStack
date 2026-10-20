import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { buildRealmPlan } from './development-realm-plan.mjs';
import { claimRoot, validateEffectiveMappers } from './development-realm-mappers.mjs';

const desired = JSON.parse(await readFile(new URL('../infra/keycloak/realms/learnstack.json', import.meta.url), 'utf8'));
const secret = 'private-test-source';
const clone = value => structuredClone(value);
const omit = (value, keys) => Object.fromEntries(Object.entries(value).filter(([key]) => !keys.includes(key)));
const manifest = { users: desired.users.map((user, index) => ({
  username: user.username, email: user.email, firstName: user.firstName, lastName: user.lastName,
  userId: user.attributes.user_id[0], tenantId: user.attributes.tenant_id[0],
  organizationId: user.attributes.organization_id?.[0] ?? null, realmRole: user.realmRoles[0],
  legacyTenantAttribute: index < 2 ? 'legacy-scope' : null,
})) };

function provider() {
  let serial = 0;
  const id = () => `provider-${serial += 1}`;
  const state = {
    realm: { realm: desired.realm, id: id(), accessTokenLifespan: 900, displayName: 'retained realm' },
    profile: JSON.parse(desired.components['org.keycloak.userprofile.UserProfileProvider'][0].config['kc.user.profile.config'][0]),
    scopes: desired.clientScopes.map(scope => ({ ...clone(scope), id: id(),
      protocolMappers: (scope.protocolMappers ?? []).map(mapper => ({ ...clone(mapper), id: id() })) })),
    clients: desired.clients.map(client => ({ ...clone(client), id: id(), protocolMappers: [],
      secret: client.clientId === 'learnstack-web' ? secret : client.secret })),
    users: desired.users.map(user => ({ ...omit(clone(user), ['realmRoles', 'credentials']), id: id(),
      requiredActions: [], credentials: [{ id: id(), type: 'password', credentialData: 'retained password metadata' }],
      roles: { realmMappings: user.realmRoles.map(name => ({ id: id(), name, composite: false, clientRole: false })) } })),
    roles: desired.roles.realm.map(role => ({ ...clone(role), id: id(), composite: false, clientRole: false })),
  };
  state.realm.defaultRole = { id: id(), name: `default-roles-${desired.realm}`, composite: true, clientRole: false };
  const writes = [];
  const reads = [];
  const get = async path => {
    reads.push(path);
    const url = new URL(path, 'http://provider/');
    const parts = url.pathname.slice(1).split('/').map(decodeURIComponent);
    let value;
    if (path === '') value = state.realm;
    else if (path === 'users/profile') value = state.profile;
    else if (parts[0] === 'users' && parts.length === 1) {
      let users = state.users;
      if (url.searchParams.has('username')) users = users.filter(user => user.username === url.searchParams.get('username'));
      const first = Number(url.searchParams.get('first') ?? 0);
      value = users.slice(first, first + Number(url.searchParams.get('max') ?? 100)).map(user => omit(user, ['roles', 'credentials']));
    } else if (parts[0] === 'users') {
      const user = state.users.find(entry => entry.id === parts[1]);
      if (!user) throw new Error('unknown fixture user');
      value = parts[2] === 'credentials' ? user.credentials : parts[2] === 'role-mappings' ? user.roles : omit(user, ['roles', 'credentials']);
    } else if (parts[0] === 'roles') value = state.roles.find(role => role.name === parts[1]);
    else if (path === 'client-scopes') value = state.scopes.map(scope => omit(scope, ['protocolMappers']));
    else if (parts[0] === 'client-scopes') value = state.scopes.find(scope => scope.id === parts[1]).protocolMappers;
    else if (parts[0] === 'clients' && parts.length === 1)
      value = url.searchParams.get('clientId') === 'account' ? [{ id: 'account-provider', clientId: 'account' }]
        : state.clients.filter(client => client.clientId === url.searchParams.get('clientId')).map(client => ({ id: client.id, clientId: client.clientId }));
    else if (parts[0] === 'clients') {
      const client = state.clients.find(entry => entry.id === parts[1]);
      if (!parts[2]) value = { ...omit(client, ['defaultClientScopes', 'optionalClientScopes', 'protocolMappers', 'secret', 'authorizationServicesEnabled']),
        ...(client.authorizationServicesEnabled === true ? { authorizationServicesEnabled: true } : {}) };
      else if (parts[2] === 'client-secret') value = { type: 'secret', value: client.secret };
      else if (parts[2] === 'protocol-mappers') value = client.protocolMappers;
      else {
        const names = parts[2] === 'default-client-scopes' ? client.defaultClientScopes : client.optionalClientScopes;
        value = names.map(name => omit(state.scopes.find(scope => scope.name === name), ['protocolMappers']));
      }
    }
    if (value === undefined) throw new Error(`unhandled fixture GET ${path}`);
    return clone(value);
  };
  const put = async (path, body) => {
    writes.push({ method: 'PUT', path, body: clone(body) });
    const parts = path.split('/').map(decodeURIComponent);
    if (path === '') Object.assign(state.realm, clone(body));
    else if (path === 'users/profile') state.profile = clone(body);
    else if (parts[0] === 'users') Object.assign(state.users.find(user => user.id === parts[1]), clone(body));
    else if (parts[0] === 'clients') {
      const client = state.clients.find(entry => entry.id === parts[1]);
      if (!parts[2]) {
        Object.assign(client, omit(clone(body), ['attributes']));
        // Pinned RepresentationToModel derives origins from redirects when an
        // omitted list falls back to an existing empty list.
        if (!Object.hasOwn(body, 'webOrigins') && client.webOrigins.length === 0)
          client.webOrigins = [...new Set(client.redirectUris.filter(uri => uri.startsWith('http'))
            .map(uri => new URL(uri).origin))];
        for (const [name, value] of Object.entries(body.attributes ?? {})) {
          if (value === null || value.trim() === '') delete client.attributes[name];
          else client.attributes[name] = value;
        }
        // Pinned ClientResource reattaches this scope when an unchanged true
        // flag is resent. Omitting the flag avoids that unrelated mutation.
        if (body.serviceAccountsEnabled === true && !client.defaultClientScopes.includes('service_account'))
          client.defaultClientScopes.push('service_account');
        // Omission disables the resource server, rather than preserving it.
        client.authorizationServicesEnabled = body.authorizationServicesEnabled === true;
        for (const name of ['client.secret.expiration.time', 'client.secret.remaining.expiration.time',
          'client.secret.rotated', 'client.secret.rotated.creation.time', 'client.secret.rotated.expiration.time'])
          delete client.attributes[name];
      }
      else {
        const scope = state.scopes.find(entry => entry.id === parts[3]);
        client[parts[2] === 'default-client-scopes' ? 'defaultClientScopes' : 'optionalClientScopes'].push(scope.name);
      }
    } else if (parts[0] === 'client-scopes') {
      const scope = state.scopes.find(entry => entry.id === parts[1]);
      if (parts[2]) Object.assign(scope.protocolMappers.find(mapper => mapper.id === parts[4]), clone(body));
      else Object.assign(scope, clone(body));
    } else throw new Error('unhandled fixture PUT');
    return 204;
  };
  const post = async (path, body) => {
    writes.push({ method: 'POST', path, body: clone(body) });
    const parts = path.split('/').map(decodeURIComponent);
    if (path === 'client-scopes') state.scopes.push({ ...clone(body), id: id(),
      protocolMappers: (body.protocolMappers ?? []).map(mapper => ({ ...clone(mapper), id: id() })) });
    else if (parts[0] === 'client-scopes')
      state.scopes.find(scope => scope.id === parts[1]).protocolMappers.push({ ...clone(body), id: id() });
    else if (path === 'partialImport') {
      assert.equal(body.ifResourceExists, 'FAIL');
      assert.deepEqual(Object.keys(body).sort(), ['ifResourceExists', 'users']);
      assert.equal(body.users.length, 1);
      const user = body.users[0];
      state.users.push({ ...omit(clone(user), ['credentials', 'realmRoles']), id: id(), requiredActions: [],
        credentials: [{ id: id(), type: 'password' }],
        roles: { realmMappings: user.realmRoles.map(name => clone(state.roles.find(role => role.name === name))) } });
      return { status: 200 };
    }
    else if (parts[0] === 'users' && parts[2] === 'role-mappings') {
      state.users.find(user => user.id === parts[1]).roles.realmMappings.push(...clone(body));
      return { status: 204 };
    } else throw new Error('unhandled fixture POST');
    return { status: 201 };
  };
  return { state, writes, reads, admin: { get, put, post } };
}

const plan = fixture => buildRealmPlan({ admin: fixture.admin, desired: clone(desired), manifest: clone(manifest), webSecret: secret });
const apply = async change => { for (const action of change.actions) await action.apply(); await change.verify(); };

function legacy(fixture) {
  const { state } = fixture;
  state.realm.accessTokenLifespan = 1800;
  state.profile.attributes = state.profile.attributes.filter(attribute => !['user_id', 'tenant_id', 'organization_id'].includes(attribute.name));
  state.profile.unmanagedAttributePolicy = 'ADMIN_EDIT';
  state.profile.attributes.push({ name: 'retained', permissions: { view: ['admin'], edit: ['admin'] } });
  state.profile.groups.push({ name: 'retained', displayHeader: 'Retained group' });
  state.scopes = state.scopes.filter(scope => scope.name !== 'learnstack-tenant-api');
  for (const client of state.clients) {
    client.defaultClientScopes = client.defaultClientScopes.filter(name => name !== 'learnstack-tenant-api');
    client.attributes['access.token.lifespan'] = '1800';
    client.attributes['retained-setting'] = 'retained';
    if (client.clientId === 'learnstack-web') { client.publicClient = true; client.secret = 'unused-public-secret'; }
  }
  state.users = state.users.slice(0, 2);
  for (const [index, user] of state.users.entries()) {
    user.attributes = { tenant_id: [manifest.users[index].legacyTenantAttribute], retained: ['untouched'] };
    user.enabled = false;
    user.requiredActions = ['UPDATE_PASSWORD'];
    user.credentials.push({ id: `mfa-${index}`, type: 'otp', credentialData: 'retained MFA' });
    user.firstName = 'Retained';
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
  assert.deepEqual(fixture.state.users[0].roles, original.users[0].roles);
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
  for (const client of fixture.state.clients) {
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
    for (const client of fixture.state.clients) {
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
      for (const client of fixture.state.clients) {
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
  assert.equal((await plan(fixture)).actions.length, 0);
  assert.equal(fixture.writes.length, 1);
});

test('creation409 permits exactly one fresh state check and never overwrites or assigns an existing account role', async () => {
  for (const compatible of [true, false]) {
    const fixture = provider(); fixture.state.users.pop();
    const originalPost = fixture.admin.post;
    fixture.admin.post = async (path, body) => {
      await originalPost(path, body);
      if (!compatible) fixture.state.users.at(-1).roles.realmMappings = [];
      return { status: 409 };
    };
    const change = await plan(fixture);
    const initialLookups = fixture.reads.filter(path => path.includes('username=')).length;
    if (compatible) await change.actions[0].apply();
    else await assert.rejects(change.actions[0].apply(), error => error.stage === 'apply' && error.reason === 'identity_conflict');
    assert.equal(fixture.writes.length, 1);
    assert.equal(fixture.reads.filter(path => path.includes('username=')).length - initialLookups, 1);
    assert.equal(fixture.writes[0].body.ifResourceExists, 'FAIL');
    if (compatible) await change.verify();
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
