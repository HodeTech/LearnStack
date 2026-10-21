import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

// Test-only in-memory provider; production entry points never import this module.
export const desired = JSON.parse(await readFile(new URL('../infra/keycloak/realms/learnstack.json', import.meta.url), 'utf8'));
export const secret = 'private-test-source';
const clone = value => structuredClone(value);
const omit = (value, keys) => Object.fromEntries(Object.entries(value).filter(([key]) => !keys.includes(key)));
// Pinned RepresentationToModel.toModel/removeEmptyString, independently modeled
// for provider storage instead of echoing the declaration back to the planner.
const storedMapper = mapper => ({ ...clone(mapper), config: Object.fromEntries(
  Object.entries(mapper.config ?? {}).filter(([, value]) => value !== null && value !== '')) });
export const manifest = { users: desired.users.map((user, index) => ({
  username: user.username, email: user.email, firstName: user.firstName, lastName: user.lastName,
  userId: user.attributes.user_id[0], tenantId: user.attributes.tenant_id[0],
  organizationId: user.attributes.organization_id?.[0] ?? null, realmRole: user.realmRoles[0],
  accountCapability: { clientId: 'account', role: 'manage-account' },
  legacyTenantAttribute: index < 2 ? 'tenant-a' : null,
})) };

export function provider() {
  let serial = 0;
  const id = () => `provider-${serial += 1}`;
  const state = {
    realm: { realm: desired.realm, enabled: true, id: id(), accessTokenLifespan: 900, displayName: 'retained realm' },
    profile: JSON.parse(desired.components['org.keycloak.userprofile.UserProfileProvider'][0].config['kc.user.profile.config'][0]),
    scopes: desired.clientScopes.map(scope => ({ ...clone(scope), id: id(),
      protocolMappers: (scope.protocolMappers ?? []).map(mapper => ({ ...storedMapper(mapper), id: id() })) })),
    clients: desired.clients.map(client => ({ ...clone(client), id: id(),
      protocolMappers: (client.protocolMappers ?? []).map(mapper => ({ ...storedMapper(mapper), id: id() })),
      secret: client.clientId === 'learnstack-web' ? secret : client.secret })),
    users: desired.users.map(user => ({ ...omit(clone(user), ['realmRoles', 'clientRoles', 'credentials']), id: id(),
      requiredActions: [], credentials: [{ id: id(), type: 'password', credentialData: 'retained password metadata' }],
      roles: { realmMappings: user.realmRoles.map(name => ({ id: id(), name, composite: false, clientRole: false })) } })),
    roles: desired.roles.realm.map(role => ({ ...clone(role), id: id(), composite: false, clientRole: false })),
    groups: [], memberships: {},
  };
  state.realm.defaultRole = { id: id(), name: `default-roles-${desired.realm}`, composite: true, clientRole: false };
  const account = state.clients.find(client => client.clientId === 'account');
  const console = state.clients.find(client => client.clientId === 'account-console');
  state.accountRoles = desired.roles.client.account.map(role => ({ ...omit(clone(role), ['composites']),
    id: id(), containerId: account.id }));
  state.roleChildren = Object.fromEntries(state.accountRoles.map(role => [role.id,
    (desired.roles.client.account.find(value => value.name === role.name).composites?.client.account ?? [])
      .map(name => state.accountRoles.find(value => value.name === name))]));
  const clientMapping = names => ({ id: state.clients.find(client => client.clientId === 'account').id, client: 'account',
    mappings: names.map(name => clone(state.accountRoles.find(role => role.name === name))) });
  for (const [index, user] of state.users.entries()) user.roles.clientMappings = {
    account: clientMapping(desired.users[index].clientRoles.account),
  };
  state.scopeMappings = { [`clients/${console.id}`]: { clientMappings: {
    account: clientMapping(['manage-account', 'view-groups']),
  } } };
  const effectiveAccountRoles = user => {
    const values = new Map();
    const visit = role => {
      if (values.has(role.id)) return;
      values.set(role.id, role);
      for (const child of state.roleChildren[role.id] ?? []) visit(child);
    };
    for (const role of [...(user.roles?.clientMappings?.account?.mappings ?? []), ...(user.roles?.realmMappings ?? [])]) visit(role);
    for (const groupId of state.memberships[user.id] ?? []) {
      let group = state.groups.find(value => value.id === groupId);
      while (group) {
        for (const name of group.clientRoles?.account ?? []) visit(state.accountRoles.find(role => role.name === name));
        group = state.groups.find(value => value.id === group.parentId);
      }
    }
    return [...values.values()].filter(role => role.containerId === state.clients.find(client => client.clientId === 'account').id);
  };

  const writes = [];
  const reads = [];
  const canEdit = attribute => attribute.permissions?.edit?.includes('admin') === true;
  const canRead = attribute => canEdit(attribute) || attribute.permissions?.view?.includes('admin') === true;
  const unmanagedReadable = () => ['ENABLED', 'ADMIN_EDIT', 'ADMIN_VIEW'].includes(state.profile.unmanagedAttributePolicy);
  const projectUser = (user, includeMetadata = false) => {
    const projected = omit(user, ['roles', 'credentials']);
    projected.attributes = Object.fromEntries(Object.entries(user.attributes ?? {}).filter(([name]) => {
      const attribute = state.profile.attributes.find(entry => entry.name === name);
      return attribute ? canRead(attribute) : unmanagedReadable();
    }));
    for (const attribute of state.profile.attributes) {
      if (canRead(attribute) && !projected.attributes[attribute.name]?.length && attribute.defaultValue != null)
        projected.attributes[attribute.name] = [attribute.defaultValue];
    }
    if (includeMetadata) projected.userProfileMetadata = {
      attributes: state.profile.attributes.filter(canRead).map(attribute => ({ name: attribute.name,
        displayName: attribute.displayName, required: false, readOnly: !canEdit(attribute),
        annotations: attribute.annotations ?? {}, validators: Object.fromEntries(Object.entries(attribute.validations ?? {})
          .map(([name, config]) => [name, { ...config, 'ignore.empty.value': true }])), multivalued: attribute.multivalued === true })),
      groups: clone(state.profile.groups ?? []),
    };
    return projected;
  };
  const get = async path => {
    reads.push(path);
    const url = new URL(path, 'http://provider/');
    const parts = url.pathname.slice(1).split('/').map(decodeURIComponent);
    let value;
    if (path === '') value = state.realm;
    else if (path === 'users/profile') value = state.profile;
    else if (path === 'groups/count') value = { count: state.groups.filter(group => group.type !== 'organization').length };
    else if (parts[0] === 'groups') {
      const search = url.searchParams.get('search');
      assert.ok(search === '' || search === ' ');
      assert.equal(url.searchParams.get('populateHierarchy'), 'false');
      assert.equal(url.searchParams.get('briefRepresentation'), 'false');
      assert.equal(url.searchParams.get('max'), '201');
      let groups = state.groups.filter(group => group.type !== 'organization');
      // An empty REST query is injected as null and lists only top-level
      // groups; a nonempty space reaches the trimmed contains-search instead.
      if (search === '') groups = groups.filter(group => group.parentId == null);
      value = groups.slice(0, 201).map(group => omit(group, ['type']));
    }
    else if (parts[0] === 'users' && parts.length === 1) {
      let users = state.users;
      // Pinned UsersResource selects its filtered branch even for exact=false;
      // the unfiltered branch excludes service accounts. Username matching is
      // case-insensitive contains unless exact=true. Brief rows omit attributes.
      // Missing briefRepresentation is full, so deleting explicit false is equivalent.
      if (!url.searchParams.has('exact') && !url.searchParams.has('username'))
        users = users.filter(user => !user.serviceAccountClientId);
      if (url.searchParams.has('username')) {
        const username = url.searchParams.get('username').toLowerCase();
        users = users.filter(user => url.searchParams.get('exact') === 'true'
          ? user.username.toLowerCase() === username : user.username.toLowerCase().includes(username));
      }
      const first = Number(url.searchParams.get('first') ?? 0);
      value = users.slice(first, first + Number(url.searchParams.get('max') ?? 100)).map(user =>
        url.searchParams.get('briefRepresentation') === 'true'
          ? Object.fromEntries(['id', 'username', 'email', 'firstName', 'lastName', 'enabled', 'emailVerified', 'createdTimestamp']
            .filter(key => Object.hasOwn(user, key)).map(key => [key, user[key]]))
          : projectUser(user, true));
    } else if (parts[0] === 'users') {
      const user = state.users.find(entry => entry.id === parts[1]);
      if (!user) throw new Error('unknown fixture user');
      if (parts[2] === 'groups') {
        let groups = (state.memberships[user.id] ?? []).map(groupId => state.groups.find(group => group.id === groupId));
        // Pinned null/null membership lookup sees all group types; supplying
        // pagination switches to the realm-only branch and conceals org rows.
        if (url.searchParams.has('first') || url.searchParams.has('max')) groups = groups.filter(group => group.type !== 'organization');
        value = groups.map(group => omit(group, ['type', 'attributes', 'realmRoles', 'clientRoles']));
      } else if (parts[2] === 'role-mappings' && parts[3] === 'clients')
        value = parts[5] === 'composite' ? effectiveAccountRoles(user) : user.roles?.clientMappings?.account?.mappings ?? [];
      else value = parts[2] === 'credentials' ? user.credentials : parts[2] === 'role-mappings' ? user.roles : projectUser(user);
    } else if (parts[0] === 'roles-by-id') value = state.roleChildren[parts[1]] ?? [];
    else if (parts.at(-1) === 'scope-mappings') value = state.scopeMappings[parts.slice(0, -1).join('/')] ?? {};
    else if (parts[0] === 'roles') value = state.roles.find(role => role.name === parts[1]);
    else if (path === 'client-scopes') value = state.scopes;
    else if (parts[0] === 'client-scopes') {
      const scope = state.scopes.find(scope => scope.id === parts[1]);
      value = parts[2] ? scope.protocolMappers ?? [] : scope;
    }
    else if (parts[0] === 'clients' && parts.length === 1)
      value = state.clients.filter(client => client.clientId === url.searchParams.get('clientId')).map(client => ({ id: client.id, clientId: client.clientId }));
    else if (parts[0] === 'clients') {
      const client = state.clients.find(entry => entry.id === parts[1]);
      if (!parts[2]) value = { ...omit(client, ['defaultClientScopes', 'optionalClientScopes', 'protocolMappers', 'secret', 'authorizationServicesEnabled']),
        ...(client.authorizationServicesEnabled === true ? { authorizationServicesEnabled: true } : {}) };
      else if (parts[2] === 'roles') value = state.accountRoles.find(role => role.name === parts[3]);
      else if (parts[2] === 'client-secret') value = { type: 'secret', value: client.secret };
      else if (parts[2] === 'protocol-mappers') value = client.protocolMappers;
      else {
        const names = parts[2] === 'default-client-scopes' ? client.defaultClientScopes : client.optionalClientScopes;
        value = names.map(name => {
          const scope = state.scopes.find(scope => scope.name === name);
          return { id: scope.id, name: scope.name };
        });
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
    else if (parts[0] === 'users') {
      const user = state.users.find(entry => entry.id === parts[1]);
      const readonly = Object.fromEntries(Object.entries(user.attributes ?? {}).filter(([name]) => {
        const attribute = state.profile.attributes.find(entry => entry.name === name);
        return attribute ? !canEdit(attribute) : !['ENABLED', 'ADMIN_EDIT'].includes(state.profile.unmanagedAttributePolicy);
      }));
      Object.assign(user, clone(body), { attributes: { ...clone(body.attributes), ...readonly } });
    }
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
      else {
        // Independent pinned ClientScopeResource.update validation: omitted
        // name is not an attribute-only PATCH and never reaches model update.
        assert.equal(body.name, scope.name, 'pinned scope update requires unchanged name');
        assert.equal(body.protocol, scope.protocol, 'pinned scope update retains protocol');
        Object.assign(scope, clone(body));
      }
    } else throw new Error('unhandled fixture PUT');
    return path === 'users/profile' ? 200 : 204;
  };
  const post = async (path, body) => {
    writes.push({ method: 'POST', path, body: clone(body) });
    const parts = path.split('/').map(decodeURIComponent);
    if (path === 'client-scopes') state.scopes.push({ ...clone(body), id: id(),
      protocolMappers: (body.protocolMappers ?? []).map(mapper => ({ ...storedMapper(mapper), id: id() })) });
    else if (parts[0] === 'client-scopes')
      state.scopes.find(scope => scope.id === parts[1]).protocolMappers.push({ ...storedMapper(body), id: id() });
    else if (parts[0] === 'clients' && parts[2] === 'protocol-mappers')
      state.clients.find(client => client.id === parts[1]).protocolMappers.push({ ...storedMapper(body), id: id() });
    else if (path === 'partialImport') {
      assert.equal(body.ifResourceExists, 'FAIL');
      assert.deepEqual(Object.keys(body).sort(), ['ifResourceExists', 'users']);
      assert.equal(body.users.length, 1);
      const user = body.users[0];
      state.users.push({ ...omit(clone(user), ['credentials', 'realmRoles', 'clientRoles']), id: id(), requiredActions: [],
        credentials: [{ id: id(), type: 'password' }],
        roles: { realmMappings: user.realmRoles.map(name => clone(state.roles.find(role => role.name === name))),
          clientMappings: { account: clientMapping(user.clientRoles.account) } } });
      return { status: 200, body: { added: 1, overwritten: 0, skipped: 0,
        results: [{ action: 'ADDED', resourceType: 'USER', resourceName: user.username, id: state.users.at(-1).id }] } };
    }
    else if (parts[0] === 'users' && parts[2] === 'role-mappings') {
      const user = state.users.find(user => user.id === parts[1]);
      if (parts[3] === 'clients') {
        user.roles.clientMappings ??= {};
        user.roles.clientMappings.account ??= clientMapping([]);
        user.roles.clientMappings.account.mappings.push(...clone(body));
      } else user.roles.realmMappings.push(...clone(body));
      return { status: 204 };
    } else throw new Error('unhandled fixture POST');
    return { status: 201 };
  };
  return { state, writes, reads, admin: { get, put, post } };
}

/** Exercise the real core and planner against the same independent test state. */
// Compose the complete planner model with independently captured stock DTOs.
// This synthetic provider exercises dispatch/readback; the real-provider proof
// independently checks the committed historical source and expectation.
export async function stockProvider() {
  const { stockConsoleProvider } = await import('./development-realm-stock-console-test-fixture.mjs');
  const stock = stockConsoleProvider('stock');
  const fixture = provider();
  const { state, reads, writes } = fixture;
  const originalGet = fixture.admin.get;
  state.realm.id = stock.capture[''].id;
  const stockNames = new Set(stock.capture['client-scopes'].map(scope => scope.name));
  state.scopes = [...state.scopes.filter(scope => !stockNames.has(scope.name)), ...clone(stock.capture['client-scopes'])];
  for (const name of ['account', 'account-console']) {
    const index = state.clients.findIndex(client => client.clientId === name);
    state.clients[index] = clone(stock.capture[`clients/${stock[name === 'account' ? 'account' : 'console'].id}`]);
  }
  state.accountRoles = ['manage-account', 'manage-account-links', 'view-groups']
    .map(name => clone(stock.capture[`clients/${stock.account.id}/roles/${name}`]));
  state.roleChildren = Object.fromEntries(state.accountRoles.map(role => [role.id,
    clone(stock.capture[`roles-by-id/${role.id}/composites`])]));
  state.roles.push(clone(stock.capture['roles/offline_access']));
  state.scopeMappings = Object.fromEntries(Object.entries(stock.capture)
    .filter(([path]) => path.endsWith('/scope-mappings')).map(([path, value]) => [path.slice(0, -'/scope-mappings'.length), clone(value)]));
  for (const user of state.users) {
    const mapping = user.roles.clientMappings.account;
    mapping.id = stock.account.id;
    mapping.mappings = mapping.mappings.map(role => omit(clone(state.accountRoles.find(value => value.name === role.name)), ['attributes']));
  }
  fixture.admin.get = async path => {
    const parts = path.split('/');
    if (parts[0] === 'clients' && parts.length === 2 && [stock.account.id, stock.console.id].includes(parts[1])) {
      reads.push(path);
      return clone(state.clients.find(client => client.id === parts[1]));
    }
    if (path.endsWith('/protocol-mappers/models')) {
      const values = await originalGet(path);
      const captured = stock.capture[path] ?? [];
      return values.map(value => clone(captured.find(mapper => mapper.id === value.id) ?? value));
    }
    if (parts[0] === 'roles-by-id' && parts.length === 2) {
      reads.push(path);
      const role = [...state.roles, ...state.accountRoles].find(role => role.id === parts[1]);
      if (!role) throw new Error('unknown stock fixture role');
      return clone(role);
    }
    return originalGet(path);
  };
  fixture.admin.delete = async path => {
    const match = /^clients\/([^/]+)\/(default|optional)-client-scopes\/([^/]+)$/.exec(path);
    assert.ok(match && match[1] === stock.console.id);
    const scope = state.scopes.find(scope => scope.id === match[3]);
    assert.ok(scope);
    const client = state.clients.find(client => client.id === stock.console.id);
    const key = `${match[2]}ClientScopes`;
    assert.ok(client[key].includes(scope.name));
    client[key] = client[key].filter(name => name !== scope.name);
    writes.push({ method: 'DELETE', path });
    return 204;
  };
  return { ...fixture, stock };
}

export function providerTransport(fixture) {
  const requests = [];
  let serial = 0;
  let bearer;
  let refresh;
  const response = (status, body) => ({ status, bytes: Buffer.from(body === undefined ? '' : JSON.stringify(body)) });
  const transport = async (url, options = {}) => {
    requests.push({ path: url.pathname + url.search, ...options });
    if (url.pathname === '/realms/learnstack/.well-known/openid-configuration')
      return response(200, { issuer: new URL('realms/learnstack', url.origin + '/').href });
    if (url.pathname === '/realms/master/protocol/openid-connect/token') {
      assert.equal(options.method, 'POST');
      assert.equal(options.form.client_id, 'admin-cli');
      if (options.form.grant_type === 'refresh_token') assert.equal(options.form.refresh_token, refresh);
      else assert.equal(options.form.grant_type, 'password');
      bearer = `test-only-access-${++serial}`;
      refresh = `test-only-refresh-${serial}`;
      return response(200, { access_token: bearer, refresh_token: refresh, expires_in: 60 });
    }
    assert.equal(options.bearer, bearer, 'every Admin exchange uses the current authentication');
    if (url.pathname === '/admin/serverinfo') return response(200, { systemInfo: { version: '26.6.2' } });
    assert.ok(url.pathname.startsWith('/admin/realms/learnstack/'));
    const path = url.pathname.slice('/admin/realms/learnstack/'.length) + url.search;
    if (options.method === 'DELETE') return response(await fixture.admin.delete(path));
    if (options.method === 'PUT') {
      const status = await fixture.admin.put(path, options.json);
      return response(status, path === 'users/profile' && status === 200 ? fixture.state.profile : undefined);
    }
    if (options.method === 'POST') {
      const value = await fixture.admin.post(path, options.json);
      return response(value.status, value.body);
    }
    return response(200, await fixture.admin.get(path));
  };
  return { requests, transport };
}
