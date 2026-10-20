import { isDeepStrictEqual } from 'node:util';
import { refuse } from './development-realm-errors.mjs';
import { identityClaims, mergeOwnedMappers, validateEffectiveMappers } from './development-realm-mappers.mjs';

const managedScope = 'learnstack-tenant-api';
const namedClients = ['learnstack-web', 'learnstack-api'];
const pageSize = 100;
const pageLimit = 10;
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const ownClientFields = ['publicClient', 'clientAuthenticatorType', 'standardFlowEnabled',
  'implicitFlowEnabled', 'directAccessGrantsEnabled', 'serviceAccountsEnabled', 'redirectUris', 'webOrigins'];
const secretRotationAttributes = ['client.secret.expiration.time', 'client.secret.remaining.expiration.time',
  'client.secret.rotated', 'client.secret.rotated.creation.time', 'client.secret.rotated.expiration.time'];
const encode = encodeURIComponent;
const copy = value => structuredClone(value);
const equal = isDeepStrictEqual;
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const omit = (value, keys) => Object.fromEntries(Object.entries(value).filter(([key]) => !keys.includes(key)));

function requireObject(value, stage) {
  if (!object(value)) refuse(stage, 'response');
  return value;
}

function array(value, stage, limit = 200) {
  if (!Array.isArray(value) || value.length > limit) refuse(stage, 'unsupported');
  return value;
}

function unique(values, key, stage, reason = 'unsupported') {
  const seen = new Set();
  for (const value of values) {
    if (!object(value) || typeof value[key] !== 'string' || !value[key] || seen.has(value[key])) refuse(stage, reason);
    seen.add(value[key]);
  }
  return values;
}

function uuidValue(value) {
  return typeof value === 'string' && uuid.test(value)
    && value !== '00000000-0000-0000-0000-000000000000';
}

function identityAttributes(user, declaration, stage) {
  const attributes = user.attributes === undefined ? {} : requireObject(user.attributes, stage);
  const merged = copy(attributes);
  const expected = { user_id: declaration.userId, tenant_id: declaration.tenantId,
    organization_id: declaration.organizationId };
  for (const name of identityClaims) {
    const current = attributes[name];
    const wanted = expected[name];
    if (current === undefined || (Array.isArray(current) && current.length === 0)) {
      if (wanted !== null) merged[name] = [wanted];
      continue;
    }
    if (!Array.isArray(current) || current.length !== 1 || typeof current[0] !== 'string')
      refuse(stage, 'identity_conflict');
    if (name === 'tenant_id' && typeof declaration.legacyTenantAttribute === 'string'
      && declaration.legacyTenantAttribute && current[0] === declaration.legacyTenantAttribute) {
      merged[name] = [wanted];
      continue;
    }
    if (wanted === null || !uuidValue(current[0]) || current[0].toLowerCase() !== wanted.toLowerCase())
      refuse(stage, 'identity_conflict');
  }
  return merged;
}

function validateInputs(desired, manifest, webSecret) {
  if (!object(desired) || desired.realm !== 'learnstack' || desired.accessTokenLifespan !== 900
    || !object(manifest) || !Array.isArray(manifest.users) || manifest.users.length !== 4
    || typeof webSecret !== 'string' || !webSecret) refuse('plan', 'unsupported');
  unique(manifest.users, 'username', 'plan', 'identity_conflict');
  const actors = new Set();
  for (const declaration of manifest.users) {
    if (!uuidValue(declaration.userId) || !uuidValue(declaration.tenantId)
      || (declaration.organizationId !== null && !uuidValue(declaration.organizationId))
      || actors.has(declaration.userId.toLowerCase())) refuse('plan', 'identity_conflict');
    actors.add(declaration.userId.toLowerCase());
    const exports = desired.users.filter(user => user.username === declaration.username);
    if (exports.length !== 1 || exports[0].email !== declaration.email
      || !equal(identityAttributes(exports[0], declaration, 'plan'), exports[0].attributes))
      refuse('plan', 'identity_conflict');
  }
}

async function allUsers(admin, stage) {
  const users = [];
  for (let page = 0; page < pageLimit; page += 1) {
    // exact=false selects the pinned provider's filtered listing, which includes
    // service accounts; the unfiltered users endpoint silently excludes them.
    const values = array(await admin.get(`users?first=${page * pageSize}&max=${pageSize}&briefRepresentation=false&exact=false`), stage, pageSize);
    users.push(...values);
    unique(users, 'id', stage, 'identity_conflict');
    if (values.length < pageSize) {
      const names = new Set();
      for (const user of users) {
        if (typeof user.username !== 'string' || names.has(user.username.toLowerCase())) refuse(stage, 'identity_conflict');
        names.add(user.username.toLowerCase());
      }
      return users;
    }
  }
  refuse(stage, 'unsupported');
}

async function readClient(admin, name, stage) {
  const matches = array(await admin.get(`clients?clientId=${encode(name)}&max=2`), stage, 2);
  if (matches.length !== 1 || matches[0].clientId !== name || !matches[0].id) refuse(stage, 'unsupported');
  const client = requireObject(await admin.get(`clients/${encode(matches[0].id)}`), stage);
  if (client.id !== matches[0].id || client.clientId !== name || client.protocol !== 'openid-connect'
    || client.bearerOnly === true || (client.clientAuthenticatorType && client.clientAuthenticatorType !== 'client-secret'))
    refuse(stage, 'unsupported');
  const defaults = unique(array(await admin.get(`clients/${encode(client.id)}/default-client-scopes`), stage), 'id', stage);
  const optional = unique(array(await admin.get(`clients/${encode(client.id)}/optional-client-scopes`), stage), 'id', stage);
  const mappers = unique(array(await admin.get(`clients/${encode(client.id)}/protocol-mappers/models`), stage), 'id', stage, 'mapper_conflict');
  let secret;
  if (name === 'learnstack-api' || client.publicClient !== true) {
    const credential = requireObject(await admin.get(`clients/${encode(client.id)}/client-secret`), stage);
    if (typeof credential.value !== 'string' || !credential.value) refuse(stage, 'secret_conflict');
    secret = credential.value;
  }
  return { client, defaults, optional, mappers, secret };
}

async function snapshot(admin, desired, manifest, stage) {
  const realm = requireObject(await admin.get(''), stage);
  if (realm.realm !== 'learnstack') refuse(stage, 'unsupported');
  const profile = requireObject(await admin.get('users/profile'), stage);
  const scopes = unique(array(await admin.get('client-scopes'), stage), 'name', stage, 'mapper_conflict');
  unique(scopes, 'id', stage, 'mapper_conflict');
  const scopeByName = new Map(scopes.map(scope => [scope.name, scope]));
  const clients = new Map();
  for (const name of namedClients) clients.set(name, await readClient(admin, name, stage));
  const effectiveNames = new Set([managedScope, 'basic']);
  for (const name of namedClients) {
    const client = desired.clients.find(value => value.clientId === name);
    for (const scopeName of [...client.defaultClientScopes, ...client.optionalClientScopes]) effectiveNames.add(scopeName);
  }
  for (const { defaults, optional } of clients.values()) {
    for (const assigned of [...defaults, ...optional]) {
      const scope = scopeByName.get(assigned.name);
      if (!scope || scope.id !== assigned.id || scope.protocol !== 'openid-connect') refuse(stage, 'unsupported');
      effectiveNames.add(assigned.name);
    }
  }
  const scopeMappers = new Map();
  for (const name of effectiveNames) {
    const scope = scopeByName.get(name);
    if (scope) scopeMappers.set(name, unique(array(await admin.get(`client-scopes/${encode(scope.id)}/protocol-mappers/models`), stage), 'id', stage, 'mapper_conflict'));
  }
  const users = await allUsers(admin, stage);
  const ownedUsers = new Map();
  for (const declaration of manifest.users) {
    const matched = users.filter(user => user.username.toLowerCase() === declaration.username.toLowerCase());
    // A profile projection can hide the stored username: pinned email-as-
    // username normalization substitutes email even during Admin REST reads.
    // Require exact lookup and census agreement before classifying an account
    // as missing; never resolve its ownership through an alternate email.
    const exact = array(await admin.get(`users?username=${encode(declaration.username)}&exact=true&max=2&briefRepresentation=false`), stage, 2);
    if (!matched.length && !exact.length) continue;
    if (matched.length !== 1 || exact.length !== 1 || matched[0].username !== declaration.username
      || exact[0].username !== declaration.username || exact[0].id !== matched[0].id)
      refuse(stage, 'identity_conflict');
    const user = requireObject(await admin.get(`users/${encode(matched[0].id)}`), stage);
    if (user.id !== matched[0].id || user.username !== declaration.username
      || user.federationLink || user.serviceAccountClientId) refuse(stage, 'identity_conflict');
    const credentials = array(await admin.get(`users/${encode(user.id)}/credentials`), stage);
    const roles = requireObject(await admin.get(`users/${encode(user.id)}/role-mappings`), stage);
    ownedUsers.set(declaration.username, { user, credentials, roles });
  }
  return { realm, profile, scopes, scopeByName, scopeMappers, clients, users, ownedUsers };
}

function mergedProfile(current, desired) {
  const attributes = unique(array(current.attributes, 'plan'), 'name', 'plan');
  if (current.groups !== undefined) unique(array(current.groups, 'plan'), 'name', 'plan');
  const merged = copy(current);
  merged.attributes = attributes.map(attribute => copy(attribute));
  const declared = JSON.parse(desired.components['org.keycloak.userprofile.UserProfileProvider'][0]
    .config['kc.user.profile.config'][0]);
  for (const name of identityClaims) {
    const canonical = declared.attributes.find(attribute => attribute.name === name);
    const index = merged.attributes.findIndex(attribute => attribute.name === name);
    if (index === -1) merged.attributes.push(copy(canonical));
    else {
      merged.attributes[index] = { ...merged.attributes[index], permissions: copy(canonical.permissions), multivalued: false };
      delete merged.attributes[index].required;
    }
  }
  return merged;
}

function mergedClient(current, wanted, webSecret) {
  const body = {};
  for (const name of ownClientFields) {
    if (name in wanted) body[name] = copy(wanted[name]);
  }
  body.attributes = { ...(current.attributes ?? {}), ...wanted.attributes };
  delete body.attributes['access.token.lifespan'];
  if (wanted.clientId === 'learnstack-web' && current.publicClient === true) body.secret = webSecret;
  return body;
}

function clientPatch(current, expected) {
  const patch = {};
  for (const field of ownClientFields)
    if (field in expected && !equal(current[field], expected[field])) patch[field] = copy(expected[field]);
  if ('secret' in expected) patch.secret = expected.secret;
  const attributes = Object.fromEntries(Object.entries(expected.attributes)
    .filter(([name, value]) => !equal(current.attributes?.[name], value)));
  // Client attributes merge in the pinned Admin REST implementation. An
  // explicit null removes this owned override; omission leaves it in force.
  if (Object.hasOwn(current.attributes ?? {}, 'access.token.lifespan')) attributes['access.token.lifespan'] = null;
  if (Object.keys(attributes).length) patch.attributes = attributes;
  if (Object.keys(patch).length) {
    // Omitting an empty origins list makes the pinned provider derive browser
    // origins from redirects. Preserve this owned server-only value explicitly.
    patch.webOrigins = copy(expected.webOrigins);
    // ClientResource disables an existing resource server when this flag is
    // omitted. Preserve its current state, including unrelated settings.
    patch.authorizationServicesEnabled = current.authorizationServicesEnabled === true;
  }
  return patch;
}

function matchesFields(actual, expected) {
  return Object.entries(expected).every(([key, value]) => equal(actual[key], value));
}

async function exactNewUser(admin, declaration, wanted, stage) {
  const values = array(await admin.get(`users?username=${encode(declaration.username)}&exact=true&max=2&briefRepresentation=false`), stage, 2);
  if (values.length !== 1 || values[0].username !== declaration.username || !values[0].id) refuse(stage, 'identity_conflict');
  const user = requireObject(await admin.get(`users/${encode(values[0].id)}`), stage);
  const expected = omit(wanted, ['credentials', 'realmRoles']);
  if (!matchesFields(user, expected) || !equal(identityAttributes(user, declaration, stage), user.attributes))
    refuse(stage, 'identity_conflict');
  if (user.requiredActions !== undefined && !equal(user.requiredActions, [])) refuse(stage, 'identity_conflict');
  return user;
}

function exactCreatedAccess(roles, credentials, roleId) {
  return Array.isArray(roles.realmMappings) && roles.realmMappings.length === 1 && roles.realmMappings[0].id === roleId
    && (!roles.clientMappings || Object.keys(roles.clientMappings).length === 0)
    && credentials.length === 1 && credentials[0].type === 'password';
}

/** A complete read-only preflight. No mutation is admitted until this resolves. */
export async function buildRealmPlan({ admin, desired, manifest, webSecret }) {
  validateInputs(desired, manifest, webSecret);
  const before = await snapshot(admin, desired, manifest, 'plan');
  const actions = [];
  const add = (label, apply) => actions.push({ label, apply });
  const desiredScopes = new Map(desired.clientScopes.map(scope => [scope.name, scope]));
  const expectedScopes = new Map(before.scopes.map(scope => [scope.name, copy(scope)]));
  const expectedMappers = new Map(before.scopeMappers);
  const requiredScopeNames = new Set();
  for (const name of namedClients) {
    const wanted = desired.clients.find(client => client.clientId === name);
    for (const scopeName of [...wanted.defaultClientScopes, ...wanted.optionalClientScopes]) requiredScopeNames.add(scopeName);
  }
  // Scope references are filled only after a successful creation/readback. The
  // plan itself holds no synthetic provider IDs.
  const scopeIds = new Map(before.scopes.map(scope => [scope.name, scope.id]));
  for (const name of requiredScopeNames) {
    const wanted = desiredScopes.get(name);
    if (!wanted) refuse('plan', 'unsupported');
    const current = before.scopeByName.get(name);
    if (current && current.protocol !== wanted.protocol) refuse('plan', 'mapper_conflict');
    if (!current) {
      expectedScopes.set(name, copy(wanted));
      expectedMappers.set(name, copy(wanted.protocolMappers ?? []));
      add('create_scope', async () => {
        const result = await admin.post('client-scopes', copy(wanted));
        if (result.status !== 201 && result.status !== 409) refuse('apply', 'status');
        const found = array(await admin.get('client-scopes'), 'apply').filter(scope => scope.name === name);
        if (found.length !== 1 || !found[0].id || !matchesFields(found[0], omit(wanted, ['protocolMappers']))) refuse('apply', 'mapper_conflict');
        const actualMappers = array(await admin.get(`client-scopes/${encode(found[0].id)}/protocol-mappers/models`), 'apply');
        if (!equal(actualMappers.map(mapper => omit(mapper, ['id'])).sort(byName),
          (wanted.protocolMappers ?? []).map(mapper => omit(mapper, ['id'])).sort(byName))) refuse('apply', 'mapper_conflict');
        scopeIds.set(name, found[0].id);
      });
    } else if (name === managedScope || name === 'basic') {
      const currentMappers = before.scopeMappers.get(name) ?? [];
      const merged = mergeOwnedMappers(currentMappers, wanted.protocolMappers, 'plan');
      expectedMappers.set(name, merged);
      for (const mapper of merged) {
        const previous = currentMappers.find(value => value.name === mapper.name);
        if (equal(previous, mapper)) continue;
        if (previous) add('update_mapper', () => admin.put(`client-scopes/${encode(current.id)}/protocol-mappers/models/${encode(previous.id)}`, copy(mapper)));
        else add('create_mapper', async () => {
          const result = await admin.post(`client-scopes/${encode(current.id)}/protocol-mappers/models`, copy(mapper));
          if (result.status !== 201 && result.status !== 409) refuse('apply', 'status');
          const values = array(await admin.get(`client-scopes/${encode(current.id)}/protocol-mappers/models`), 'apply');
          const found = values.filter(value => value.name === mapper.name);
          if (found.length !== 1 || !matchesFields(found[0], mapper)) refuse('apply', 'mapper_conflict');
        });
      }
      if (name === managedScope) {
        const attributes = { ...current.attributes, ...wanted.attributes };
        const body = { attributes };
        expectedScopes.set(name, { ...current, ...body });
        if (!equal(current.attributes, attributes)) add('update_scope', () => admin.put(`client-scopes/${encode(current.id)}`, copy(body)));
      }
    }
  }
  const profile = mergedProfile(before.profile, desired);
  if (!equal(profile, before.profile)) add('update_profile', () => admin.put('users/profile', copy(profile)));
  if (before.realm.accessTokenLifespan !== 900) add('update_lifespan', () => admin.put('', { accessTokenLifespan: 900 }));
  const expectedClients = new Map();
  const expectedAssignments = new Map();
  for (const name of namedClients) {
    const baseline = before.clients.get(name);
    const wanted = desired.clients.find(client => client.clientId === name);
    // ClientResource can create/delete nonmanifest service accounts or move its
    // scope as a side effect. Such transitions need explicit operator handling.
    if (baseline.client.serviceAccountsEnabled !== wanted.serviceAccountsEnabled)
      refuse('plan', 'unsupported');
    if (name === 'learnstack-web' && baseline.client.publicClient !== true && baseline.secret !== webSecret) refuse('plan', 'secret_conflict');
    const body = mergedClient(baseline.client, wanted, webSecret);
    expectedClients.set(name, { ...baseline.client, ...omit(body, ['secret']) });
    const patch = clientPatch(baseline.client, body);
    if (Object.keys(patch).length) {
      // Stock ClientResource PUT clears configured expiry/rotation state.
      // Ordinary client.secret.creation.time is not rotation state and stays.
      if (secretRotationAttributes.some(attribute => Object.hasOwn(baseline.client.attributes ?? {}, attribute)))
        refuse('plan', 'unsupported');
      if (baseline.client.serviceAccountsEnabled === false
        && (before.users.some(user => user.serviceAccountClientId === name)
          || baseline.defaults.some(scope => scope.name === 'service_account')))
        refuse('plan', 'unsupported');
      add('update_client', () => admin.put(`clients/${encode(baseline.client.id)}`, copy(patch)));
    }
    const defaults = new Set(baseline.defaults.map(scope => scope.name));
    const optional = new Set(baseline.optional.map(scope => scope.name));
    for (const scopeName of wanted.defaultClientScopes) {
      if (optional.has(scopeName)) refuse('plan', 'unsupported');
      defaults.add(scopeName);
      if (!baseline.defaults.some(scope => scope.name === scopeName))
        add('attach_default_scope', () => admin.put(`clients/${encode(baseline.client.id)}/default-client-scopes/${encode(scopeIds.get(scopeName))}`));
    }
    for (const scopeName of wanted.optionalClientScopes) {
      if (defaults.has(scopeName)) continue;
      optional.add(scopeName);
      if (!baseline.optional.some(scope => scope.name === scopeName))
        add('attach_optional_scope', () => admin.put(`clients/${encode(baseline.client.id)}/optional-client-scopes/${encode(scopeIds.get(scopeName))}`));
    }
    expectedAssignments.set(name, { defaults, optional });
    const effective = [...new Set([...defaults, ...optional])].flatMap(scopeName => {
      const mappers = expectedMappers.get(scopeName);
      if (!mappers) refuse('plan', 'unsupported');
      return mappers;
    });
    validateEffectiveMappers([...baseline.mappers, ...effective]);
  }
  // A manifest actor may not already belong to a differently named account.
  for (const user of before.users) {
    const values = user.attributes?.user_id;
    if (!Array.isArray(values)) continue;
    for (const value of values) {
      const declaration = manifest.users.find(entry => typeof value === 'string' && entry.userId.toLowerCase() === value.toLowerCase());
      if (declaration && user.username !== declaration.username) refuse('plan', 'identity_conflict');
    }
  }
  const expectedUsers = new Map();
  for (const declaration of manifest.users) {
    const existing = before.ownedUsers.get(declaration.username);
    if (existing) {
      const attributes = identityAttributes(existing.user, declaration, 'plan');
      expectedUsers.set(declaration.username, { ...existing.user, attributes });
      if (!equal(attributes, existing.user.attributes ?? {})) {
        // A supplied attribute map replaces profile values in 26.6.2. Include
        // unchanged ordinary profile fields, but no credentials/actions/enabled.
        const body = { attributes, ...Object.fromEntries(['username', 'email', 'firstName', 'lastName']
          .filter(key => key in existing.user).map(key => [key, existing.user[key]])) };
        add('update_user_attributes', () => admin.put(`users/${encode(existing.user.id)}`, copy(body)));
      }
    } else {
      if (before.users.some(user => typeof user.email === 'string' && user.email.toLowerCase() === declaration.email.toLowerCase())) refuse('plan', 'identity_conflict');
      const wanted = copy(desired.users.find(user => user.username === declaration.username));
      if (!equal(wanted.realmRoles, [declaration.realmRole])) refuse('plan', 'unsupported');
      const role = requireObject(await admin.get(`roles/${encode(declaration.realmRole)}`), 'plan');
      if (!['tenant-admin', 'tenant-learner'].includes(declaration.realmRole)
        || !role.id || role.name !== declaration.realmRole || role.composite !== false || role.clientRole !== false)
        refuse('plan', 'unsupported');
      add('create_user', async () => {
        // Pinned RealmAdminResource.partialImport wraps one named user in its
        // own transaction. Unlike POST users, this creates credentials and the
        // declared inert role atomically. FAIL never enters the overwrite path.
        const representation = Object.fromEntries(['username', 'email', 'emailVerified', 'enabled',
          'firstName', 'lastName', 'credentials', 'realmRoles', 'attributes'].map(key => [key, copy(wanted[key])]));
        const result = await admin.post('partialImport', { ifResourceExists: 'FAIL', users: [representation] });
        if (result.status !== 200 && result.status !== 409) refuse('apply', 'status');
        const created = await exactNewUser(admin, declaration, wanted, 'apply');
        const roles = requireObject(await admin.get(`users/${encode(created.id)}/role-mappings`), 'apply');
        const credentials = array(await admin.get(`users/${encode(created.id)}/credentials`), 'apply');
        if (!exactCreatedAccess(roles, credentials, role.id)) refuse('apply', 'identity_conflict');
        expectedUsers.set(declaration.username, created);
      });
    }
  }

  return { actions, verify: async () => {
    const after = await snapshot(admin, desired, manifest, 'readback');
    const check = condition => { if (!condition) refuse('readback', 'readback_conflict'); };
    check(after.realm.accessTokenLifespan === 900
      && equal(omit(after.realm, ['accessTokenLifespan']), omit(before.realm, ['accessTokenLifespan'])));
    check(equal(after.profile, profile));
    for (const [name, expected] of expectedScopes) {
      const actual = after.scopeByName.get(name);
      check(Boolean(actual) && matchesFields(actual, omit(expected, ['id', 'protocolMappers'])));
      if (before.scopeByName.has(name)) check(actual.id === before.scopeByName.get(name).id);
      if (expectedMappers.has(name)) {
        const actualMappers = after.scopeMappers.get(name) ?? [];
        check(equal(actualMappers.map(mapper => omit(mapper, ['id'])).sort(byName),
          expectedMappers.get(name).map(mapper => omit(mapper, ['id'])).sort(byName)));
        for (const original of before.scopeMappers.get(name) ?? [])
          check(actualMappers.some(mapper => mapper.name === original.name && mapper.id === original.id));
      }
    }
    for (const name of namedClients) {
      const actual = after.clients.get(name);
      const baseline = before.clients.get(name);
      const wanted = desired.clients.find(client => client.clientId === name);
      check(equal(omit(actual.client, ['secret', 'defaultClientScopes', 'optionalClientScopes']),
        omit(expectedClients.get(name), ['secret', 'defaultClientScopes', 'optionalClientScopes'])));
      check(actual.secret === (name === 'learnstack-web' ? webSecret : baseline.secret));
      check(equal(actual.mappers, baseline.mappers));
      for (const [kind, required] of [['defaults', wanted.defaultClientScopes], ['optional', wanted.optionalClientScopes]]) {
        const names = new Set(actual[kind].map(scope => scope.name));
        check(equal(names, expectedAssignments.get(name)[kind]));
        check(baseline[kind].every(scope => actual[kind].some(value => value.id === scope.id)));
        check(required.every(scopeName => names.has(scopeName)
          || (kind === 'optional' && actual.defaults.some(scope => scope.name === scopeName))));
      }
    }
    for (const previous of before.users) {
      const current = after.users.find(user => user.id === previous.id);
      check(Boolean(current));
      if (!before.ownedUsers.has(previous.username)) check(equal(current, previous));
    }
    check(after.users.length === before.users.length + manifest.users.filter(entry => !before.ownedUsers.has(entry.username)).length);
    for (const declaration of manifest.users) {
      const actual = after.ownedUsers.get(declaration.username);
      const original = before.ownedUsers.get(declaration.username);
      check(Boolean(actual) && expectedUsers.has(declaration.username));
      check(equal(actual.user, expectedUsers.get(declaration.username)));
      check(equal(identityAttributes(actual.user, declaration, 'readback'), actual.user.attributes));
      if (original) check(equal(actual.credentials, original.credentials) && equal(actual.roles, original.roles));
      else {
        const role = actual.roles.realmMappings?.find(value => value.name === declaration.realmRole);
        check(Boolean(role) && role.composite === false && role.clientRole === false
          && exactCreatedAccess(actual.roles, actual.credentials, role.id));
      }
    }
  } };
}

function byName(left, right) { return left.name.localeCompare(right.name); }
