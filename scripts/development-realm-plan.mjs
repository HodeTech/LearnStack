import { isDeepStrictEqual } from 'node:util';
import { refuse } from './development-realm-errors.mjs';
import { identityClaims, mergeOwnedMappers, validateEffectiveMappers } from './development-realm-mappers.mjs';
import { readRealmGroups, readUserGroupIds } from './development-realm-groups.mjs';
import { exactCreatedAccountAccess, planAccountSelfService } from './development-realm-account.mjs';

const managedScope = 'learnstack-tenant-api';
const namedClients = ['learnstack-web', 'learnstack-api'];
const pageSize = 100;
const pageLimit = 10;
const bodyLimit = 256 * 1024;
const generatedIdBytes = '0'.repeat(36);
// New-user Admin DTO fields (timestamp, flags, credential type names and access
// booleans) are provider-generated. Reserve more than their pinned fixed shape;
// profile metadata and the UUID are budgeted separately below.
const newUserResponseReserve = 1024;
const profileAttributeFields = new Set(['name', 'displayName', 'validations', 'annotations',
  'required', 'permissions', 'selector', 'group', 'multivalued', 'defaultValue']);
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const actorUuid = /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
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

function preflightBody(body) {
  if (body === undefined) return;
  if (Buffer.byteLength(JSON.stringify(body), 'utf8') > bodyLimit) refuse('plan', 'body_limit');
}

const jsonBytes = value => Buffer.byteLength(JSON.stringify(value), 'utf8');

function preflightResponse(value, reserve = 0) {
  if (jsonBytes(value) + reserve > bodyLimit) refuse('plan', 'body_limit');
}

function validateProjectedResponses({ before, profile, scopes, mappers, clients, assignments, users, desired, manifest, webSecret }) {
  preflightResponse({ ...before.realm, accessTokenLifespan: 900 });
  preflightResponse(profile);
  // These UUID placeholders are used only for byte accounting. Provider IDs
  // in the plan/apply/readback state remain exclusively authoritative reads.
  const projectedScopes = new Map([...scopes].map(([name, scope]) => [name, {
    ...scope, id: scope.id ?? generatedIdBytes,
    ...(mappers.has(name) ? { protocolMappers: mappers.get(name).map(mapper => ({ ...mapper, id: mapper.id ?? generatedIdBytes })) } : {}),
  }]));
  preflightResponse([...projectedScopes.values()]);
  for (const values of mappers.values())
    preflightResponse(values.map(mapper => ({ ...mapper, id: mapper.id ?? generatedIdBytes })));
  for (const name of namedClients) {
    const assigned = assignments.get(name);
    preflightResponse({ ...clients.get(name), defaultClientScopes: [...assigned.defaults], optionalClientScopes: [...assigned.optional] });
    preflightResponse({ type: 'secret', value: name === 'learnstack-web' ? webSecret : before.clients.get(name).secret });
    for (const kind of ['defaults', 'optional'])
      preflightResponse([...assigned[kind]].map(scopeName => projectedScopes.get(scopeName)));
  }
  // Full user listings repeat profile metadata in every row. Count the whole
  // projected profile plus REST field/implicit-validator overhead, including
  // when the current realm has no ordinary-user metadata template. Counts are
  // independent ceilings: this conservative byte budget can bind earlier.
  const validators = profile.attributes.reduce((count, attribute) => count + Object.keys(attribute.validations ?? {}).length, 0);
  const metadataGrowth = Math.max(0, jsonBytes(profile) - jsonBytes(before.profile))
    + (equal(profile, before.profile) ? 0 : identityClaims.length * 128);
  const observedMetadata = before.users.filter(user => !user.serviceAccountClientId && object(user.userProfileMetadata))
    .map(user => jsonBytes(user.userProfileMetadata));
  const metadataFieldBytes = Buffer.byteLength(',"userProfileMetadata":');
  const metadataBytes = metadataFieldBytes + (observedMetadata.length ? Math.max(...observedMetadata) + metadataGrowth
    : jsonBytes(profile) + profile.attributes.length * 128 + validators * 32 + 256);
  const projectedUsers = before.users.map(user => ({
    value: omit({ ...user, ...users.get(user.username) }, ['userProfileMetadata']),
    reserve: user.userProfileMetadata ? metadataFieldBytes + jsonBytes(user.userProfileMetadata) + metadataGrowth : metadataBytes,
  }));
  for (const declaration of manifest.users) {
    const existing = before.ownedUsers.get(declaration.username);
    if (existing) preflightResponse(users.get(declaration.username));
    else {
      const wanted = desired.users.find(user => user.username === declaration.username);
      const projected = { ...omit(wanted, ['credentials', 'realmRoles', 'clientRoles']), id: generatedIdBytes };
      const reserve = newUserResponseReserve + metadataBytes;
      preflightResponse(projected, reserve);
      projectedUsers.push({ value: projected, reserve });
    }
  }
  // Insertions can move page boundaries. Budget the largest possible 100
  // records, including JSON array punctuation, without relying on ordering.
  const largestPage = projectedUsers.map(({ value, reserve }) => jsonBytes(value) + reserve)
    .sort((left, right) => right - left).slice(0, pageSize);
  if (2 + Math.max(0, largestPage.length - 1) + largestPage.reduce((sum, size) => sum + size, 0) > bodyLimit)
    refuse('plan', 'body_limit');
}

function adminCanEdit(attribute) {
  if (['username', 'email'].includes(attribute.name)
    && (attribute.permissions == null
      || ((attribute.permissions.view?.length ?? 0) === 0 && (attribute.permissions.edit?.length ?? 0) === 0))) return true;
  return Array.isArray(attribute.permissions?.edit) && attribute.permissions.edit.includes('admin');
}

function adminCanRead(attribute) {
  // Pinned USER_API view permission is the union of view and edit roles.
  return adminCanEdit(attribute)
    || (Array.isArray(attribute.permissions?.view) && attribute.permissions.view.includes('admin'));
}

function plainAttributeProjection(attribute) {
  return attribute.defaultValue == null && attribute.selector == null
    && Object.keys(attribute).every(key => profileAttributeFields.has(key));
}

function validateIdentityVisibility(profile, stage) {
  const attributes = unique(array(profile.attributes, stage), 'name', stage);
  if (profile.groups !== undefined) unique(array(profile.groups, stage), 'name', stage);
  for (const name of identityClaims) {
    const attribute = attributes.find(value => value.name === name);
    // Admin representations are profile projections, not raw storage reads.
    // Missing values prove absence only when this policy exposes unmanaged
    // attributes, or a managed definition exposes the actual stored values.
    if (attribute ? !adminCanRead(attribute) || !plainAttributeProjection(attribute)
      : !['ENABLED', 'ADMIN_EDIT', 'ADMIN_VIEW'].includes(profile.unmanagedAttributePolicy))
      refuse(stage, 'identity_conflict');
  }
}

function validateAttributePreservation(profile, creating = false) {
  // A full attribute PUT can persist a synthesized default or remove a
  // writable value hidden by an unqualified projection. Readonly hidden
  // attributes are preserved by the provider and need no policy expansion.
  for (const attribute of profile.attributes)
    if ((adminCanEdit(attribute) || (creating && adminCanRead(attribute)))
      && (!adminCanRead(attribute) || !plainAttributeProjection(attribute)))
      refuse('plan', 'unsupported');
}

function uuidValue(value) {
  return typeof value === 'string' && uuid.test(value)
    && value !== '00000000-0000-0000-0000-000000000000';
}

function legacyDeclaration(declaration) {
  return declaration.legacyTenantAttribute === 'tenant-a' && declaration.organizationId === null;
}

function qualifiedExistingActor(user, declaration, stage) {
  const actor = user.attributes?.user_id;
  if (Array.isArray(actor) && actor.length === 1 && typeof actor[0] === 'string'
    && actor[0].toLowerCase() === declaration.userId.toLowerCase()) return;
  // A public registration matching a reserved username is not a seed identity.
  // Only the two historical accounts have this explicit migration discriminator.
  if ((actor === undefined || (Array.isArray(actor) && actor.length === 0))
    && legacyDeclaration(declaration) && equal(user.attributes?.tenant_id, ['tenant-a'])) return;
  refuse(stage, 'identity_conflict');
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
    if (name === 'tenant_id' && legacyDeclaration(declaration) && current[0] === 'tenant-a') {
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
    || !Array.isArray(desired.users) || desired.users.length !== 4
    || !object(manifest) || !Array.isArray(manifest.users) || manifest.users.length !== 4
    || typeof webSecret !== 'string' || !webSecret) refuse('plan', 'unsupported');
  unique(manifest.users, 'username', 'plan', 'identity_conflict');
  unique(desired.users, 'username', 'plan', 'identity_conflict');
  // SeedIdentityManifest qualifies the two historical personas by declaration.
  // Tooling validates its bounded, tenant-wide projection without branching on
  // institution names, hosts or persona usernames.
  const legacy = manifest.users.filter(user => user.legacyTenantAttribute != null);
  if (legacy.length !== 0 && (legacy.length !== 2
    || new Set(legacy.map(user => user.tenantId)).size !== 1
    || !equal(new Set(legacy.map(user => user.realmRole)), new Set(['tenant-admin', 'tenant-learner']))))
    refuse('plan', 'identity_conflict');
  const actors = new Set();
  const scopes = new Set(manifest.users.flatMap(user => [user.tenantId, user.organizationId])
    .filter(value => typeof value === 'string').map(value => value.toLowerCase()));
  for (const declaration of manifest.users) {
    if (!uuidValue(declaration.userId) || !actorUuid.test(declaration.userId) || !uuidValue(declaration.tenantId)
      || (declaration.organizationId !== null && !uuidValue(declaration.organizationId))
      || (declaration.organizationId !== null && declaration.organizationId.toLowerCase() === declaration.tenantId.toLowerCase())
      || (declaration.legacyTenantAttribute != null && !legacyDeclaration(declaration))
      || scopes.has(declaration.userId.toLowerCase())
      || actors.has(declaration.userId.toLowerCase())) refuse('plan', 'identity_conflict');
    actors.add(declaration.userId.toLowerCase());
    const exports = desired.users.filter(user => user.username === declaration.username);
    if (!equal(declaration.accountCapability, { clientId: 'account', role: 'manage-account' })
      || exports.length !== 1 || exports[0].email !== declaration.email
      || exports[0].firstName !== declaration.firstName || exports[0].lastName !== declaration.lastName
      || !['tenant-admin', 'tenant-learner'].includes(declaration.realmRole)
      || !equal(exports[0].realmRoles, [declaration.realmRole])
      || exports[0].enabled !== true || exports[0].emailVerified !== true
      || !Array.isArray(exports[0].credentials) || exports[0].credentials.length !== 1
      || !object(exports[0].credentials[0])
      || exports[0].credentials[0].type !== 'password' || exports[0].credentials[0].temporary !== false
      || typeof exports[0].credentials[0].value !== 'string' || !exports[0].credentials[0].value
      || !equal(exports[0].clientRoles, { account: ['manage-account'] })
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
        // External storage may decorate profile metadata and hide protected
        // values independently of the realm configuration. Its actor census
        // cannot be qualified through these bounded Admin REST projections.
        if (user.federationLink || user.id.startsWith('f:')) refuse(stage, 'identity_conflict');
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
    || client.enabled !== true || client.consentRequired === true
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
  if (realm.realm !== 'learnstack' || realm.enabled !== true) refuse(stage, 'unsupported');
  const profile = requireObject(await admin.get('users/profile'), stage);
  validateIdentityVisibility(profile, stage);
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
    qualifiedExistingActor(user, declaration, stage);
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
  if (Array.isArray(current.redirectUris) && Array.isArray(body.redirectUris)
    && new Set(current.redirectUris).size === current.redirectUris.length
    && equal([...current.redirectUris].sort(), [...body.redirectUris].sort()))
    body.redirectUris = copy(current.redirectUris);
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
  const expected = omit(wanted, ['credentials', 'realmRoles', 'clientRoles']);
  if (!matchesFields(user, expected) || !equal(identityAttributes(user, declaration, stage), user.attributes))
    refuse(stage, 'identity_conflict');
  if (user.requiredActions !== undefined && !equal(user.requiredActions, [])) refuse(stage, 'identity_conflict');
  return user;
}

function exactCreatedAccess(roles, credentials, roleId, account) {
  return Array.isArray(roles.realmMappings) && roles.realmMappings.length === 1 && roles.realmMappings[0].id === roleId
    && exactCreatedAccountAccess(roles, account.account, account.role)
    && credentials.length === 1 && credentials[0].type === 'password';
}

/** A complete read-only preflight. No mutation is admitted until this resolves. */
export async function buildRealmPlan({ admin, desired, manifest, webSecret }) {
  validateInputs(desired, manifest, webSecret);
  const before = await snapshot(admin, desired, manifest, 'plan');
  const actions = [];
  const add = (label, apply, body) => {
    preflightBody(body);
    actions.push({ label, apply });
  };
  const desiredScopes = new Map(desired.clientScopes.map(scope => [scope.name, scope]));
  const expectedScopes = new Map(before.scopes.map(scope => [scope.name, copy(scope)]));
  const expectedMappers = new Map(before.scopeMappers);
  const changedScopeIds = new Set();
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
      }, wanted);
    } else if (name === managedScope || name === 'basic') {
      const currentMappers = before.scopeMappers.get(name) ?? [];
      const merged = mergeOwnedMappers(currentMappers, wanted.protocolMappers, 'plan');
      expectedMappers.set(name, merged);
      for (const mapper of merged) {
        const previous = currentMappers.find(value => value.name === mapper.name);
        if (equal(previous, mapper)) continue;
        changedScopeIds.add(current.id);
        if (previous) add('update_mapper', () => admin.put(`client-scopes/${encode(current.id)}/protocol-mappers/models/${encode(previous.id)}`, copy(mapper)), mapper);
        else add('create_mapper', async () => {
          const result = await admin.post(`client-scopes/${encode(current.id)}/protocol-mappers/models`, copy(mapper));
          if (result.status !== 201 && result.status !== 409) refuse('apply', 'status');
          const values = array(await admin.get(`client-scopes/${encode(current.id)}/protocol-mappers/models`), 'apply');
          const found = values.filter(value => value.name === mapper.name);
          if (found.length !== 1 || !matchesFields(found[0], mapper)) refuse('apply', 'mapper_conflict');
        }, mapper);
      }
      if (name === managedScope) {
        const attributes = { ...current.attributes, ...wanted.attributes };
        // Pinned ClientScopeResource validates name even for an attribute-only
        // update. Carry the unchanged identity/protocol without replaying the
        // full representation, mappers or unrelated descriptive fields.
        const body = { name: current.name, protocol: current.protocol, attributes };
        expectedScopes.set(name, { ...current, ...body });
        if (!equal(current.attributes, attributes)) add('update_scope', () => admin.put(`client-scopes/${encode(current.id)}`, copy(body)), body);
      }
    }
  }
  const profile = mergedProfile(before.profile, desired);
  if (!equal(profile, before.profile)) add('update_profile', () => admin.put('users/profile', copy(profile)), profile);
  if (before.realm.accessTokenLifespan !== 900) add('update_lifespan', () => admin.put('', { accessTokenLifespan: 900 }), { accessTokenLifespan: 900 });
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
      add('update_client', () => admin.put(`clients/${encode(baseline.client.id)}`, copy(patch)), patch);
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
  // Mapper qualification precedes inherited-source reads, so an unsafe
  // aggregation customization always refuses without being silently repaired.
  const beforeGroups = await readRealmGroups(admin, before.users, 'plan');
  const account = await planAccountSelfService({ admin, desired, manifest, ownedUsers: before.ownedUsers,
    add, preflightResponse, changedScopeIds });
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
        validateAttributePreservation(before.profile);
        // A supplied attribute map replaces profile values in 26.6.2. Include
        // unchanged ordinary profile fields, but no credentials/actions/enabled.
        const body = { attributes, ...Object.fromEntries(['username', 'email', 'firstName', 'lastName']
          .filter(key => key in existing.user).map(key => [key, existing.user[key]])) };
        add('update_user_attributes', () => admin.put(`users/${encode(existing.user.id)}`, copy(body)), body);
      }
    } else {
      validateAttributePreservation(before.profile, true);
      if (before.users.some(user => typeof user.email === 'string' && user.email.toLowerCase() === declaration.email.toLowerCase())) refuse('plan', 'identity_conflict');
      const wanted = copy(desired.users.find(user => user.username === declaration.username));
      if (!equal(wanted.realmRoles, [declaration.realmRole])) refuse('plan', 'unsupported');
      const role = requireObject(await admin.get(`roles/${encode(declaration.realmRole)}`), 'plan');
      if (!['tenant-admin', 'tenant-learner'].includes(declaration.realmRole)
        || !role.id || role.name !== declaration.realmRole || role.composite !== false || role.clientRole !== false)
        refuse('plan', 'unsupported');
      const representation = Object.fromEntries(['username', 'email', 'emailVerified', 'enabled',
        'firstName', 'lastName', 'credentials', 'realmRoles', 'clientRoles', 'attributes'].map(key => [key, copy(wanted[key])]));
      const body = { ifResourceExists: 'FAIL', users: [representation] };
      add('create_user', async () => {
        // Pinned RealmAdminResource.partialImport wraps one named user in its
        // own transaction. Unlike POST users, this creates credentials and the
        // declared inert role and account capability atomically. FAIL never overwrites.
        const result = await admin.post('partialImport', copy(body));
        // Only ADDED proves that this transaction installed the declared
        // credential. An exact-looking concurrent account cannot prove it.
        if (result.status === 409) refuse('apply', 'identity_conflict');
        if (result.status !== 200) refuse('apply', 'status');
        const receipt = result.body;
        const added = receipt?.results;
        if (!object(receipt) || receipt.added !== 1 || receipt.overwritten !== 0 || receipt.skipped !== 0
          || receipt.errorMessage != null || !Array.isArray(added) || added.length !== 1
          || !object(added[0])
          || added[0].action !== 'ADDED' || added[0].resourceType !== 'USER'
          || added[0].resourceName !== declaration.username || typeof added[0].id !== 'string' || !added[0].id)
          refuse('apply', 'identity_conflict');
        const created = await exactNewUser(admin, declaration, wanted, 'apply');
        if (created.id !== added[0].id) refuse('apply', 'identity_conflict');
        const roles = requireObject(await admin.get(`users/${encode(created.id)}/role-mappings`), 'apply');
        const credentials = array(await admin.get(`users/${encode(created.id)}/credentials`), 'apply');
        const assigned = await readUserGroupIds(admin, created.id, 'apply');
        if (!exactCreatedAccess(roles, credentials, role.id, account) || assigned.size !== 0) refuse('apply', 'identity_conflict');
        expectedUsers.set(declaration.username, created);
      }, body);
    }
  }

  // Every resulting collection must fit the same bounded readers used by
  // apply/readback and a subsequent explicit run, before any action escapes.
  array([...expectedScopes.values()], 'plan');
  array(profile.attributes, 'plan');
  if (profile.groups !== undefined) array(profile.groups, 'plan');
  for (const mappers of expectedMappers.values()) array(mappers, 'plan');
  for (const { defaults, optional } of expectedAssignments.values()) {
    array([...defaults], 'plan');
    array([...optional], 'plan');
  }
  if (before.users.length + manifest.users.filter(entry => !before.ownedUsers.has(entry.username)).length
    >= pageSize * pageLimit) refuse('plan', 'unsupported');
  validateProjectedResponses({ before, profile, scopes: expectedScopes, mappers: expectedMappers,
    clients: expectedClients, assignments: expectedAssignments, users: expectedUsers, desired, manifest, webSecret });

  return { actions, verify: async () => {
    const after = await snapshot(admin, desired, manifest, 'readback');
    await account.verify(after.ownedUsers);
    const afterGroups = await readRealmGroups(admin, after.users, 'readback');
    const check = condition => { if (!condition) refuse('readback', 'readback_conflict'); };
    check(after.realm.accessTokenLifespan === 900
      && equal(omit(after.realm, ['accessTokenLifespan']), omit(before.realm, ['accessTokenLifespan'])));
    check(equal(after.profile, profile));
    check(equal(afterGroups.groups, beforeGroups.groups));
    for (const [userId, assigned] of beforeGroups.memberships)
      check(equal(afterGroups.memberships.get(userId), assigned));
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
      // Full list rows include metadata derived from the realm profile. Its
      // owned definition changes are verified independently above; every
      // stored account field remains part of this preservation comparison.
      if (!before.ownedUsers.has(previous.username))
        check(equal(omit(current, ['userProfileMetadata']), omit(previous, ['userProfileMetadata'])));
    }
    check(after.users.length === before.users.length + manifest.users.filter(entry => !before.ownedUsers.has(entry.username)).length);
    for (const declaration of manifest.users) {
      const actual = after.ownedUsers.get(declaration.username);
      const original = before.ownedUsers.get(declaration.username);
      check(Boolean(actual) && expectedUsers.has(declaration.username));
      check(equal(actual.user, expectedUsers.get(declaration.username)));
      check(equal(identityAttributes(actual.user, declaration, 'readback'), actual.user.attributes));
      if (original) check(equal(actual.credentials, original.credentials));
      else {
        // Atomic addUser(false,false) skips default roles AND default groups.
        check(afterGroups.memberships.get(actual.user.id)?.size === 0);
        const role = actual.roles.realmMappings?.find(value => value.name === declaration.realmRole);
        check(Boolean(role) && role.composite === false && role.clientRole === false
          && exactCreatedAccess(actual.roles, actual.credentials, role.id, account));
      }
    }
  } };
}

function byName(left, right) { return left.name.localeCompare(right.name); }
