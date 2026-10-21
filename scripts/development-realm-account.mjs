import { isDeepStrictEqual as equal } from 'node:util';
import { refuse } from './development-realm-errors.mjs';
import { accountMapperRepresentation, validateAccountMappers } from './development-realm-mappers.mjs';

const encode = encodeURIComponent;
const copy = value => structuredClone(value);
const omit = (value, keys) => Object.fromEntries(Object.entries(value).filter(([key]) => !keys.includes(key)));
const ordered = values => [...values].sort((left, right) => left.id.localeCompare(right.id));
const allowedNames = ['manage-account', 'manage-account-links', 'view-groups'];
const mapperName = 'learnstack-account-self-service-roles';

function list(value, stage) {
  if (!Array.isArray(value) || value.length > 200
    || value.some(item => !item || typeof item.id !== 'string' || !item.id)
    || new Set(value.map(item => item.id)).size !== value.length) refuse(stage, 'unsupported');
  return value;
}

function sameRole(actual, expected) {
  return actual?.id === expected.id && actual.name === expected.name
    && actual.containerId === expected.containerId && actual.clientRole === true
    && actual.composite === expected.composite;
}

async function client(admin, name, stage) {
  const found = list(await admin.get(`clients?clientId=${encode(name)}&max=2`), stage);
  if (found.length !== 1 || found[0].clientId !== name) refuse(stage, 'unsupported');
  const value = await admin.get(`clients/${encode(found[0].id)}`);
  if (value.id !== found[0].id || value.clientId !== name || value.protocol !== 'openid-connect'
    || value.enabled !== true || value.publicClient !== true || value.fullScopeAllowed !== false
    || value.consentRequired === true || value.rootUrl !== '${authBaseUrl}'
    || value.baseUrl !== '/realms/learnstack/account/'
    || !equal(value.redirectUris, ['/realms/learnstack/account/*']) || !equal(value.webOrigins, [])
    || value.attributes?.['post.logout.redirect.uris'] !== '+'
    || value.bearerOnly === true || value.standardFlowEnabled !== true || value.implicitFlowEnabled !== false
    || value.directAccessGrantsEnabled !== false || value.serviceAccountsEnabled !== false
    || (value.clientAuthenticatorType && value.clientAuthenticatorType !== 'client-secret')) refuse(stage, 'unsupported');
  if (name === 'account-console' && value.attributes?.['pkce.code.challenge.method'] !== 'S256') refuse(stage, 'unsupported');
  return value;
}

async function snapshot(admin, stage) {
  const account = await client(admin, 'account', stage);
  const console = await client(admin, 'account-console', stage);
  if (account.id === console.id) refuse(stage, 'unsupported');
  const roles = new Map();
  const children = new Map();
  for (const name of allowedNames) {
    const role = await admin.get(`clients/${encode(account.id)}/roles/${encode(name)}`);
    if (!role?.id || role.name !== name || role.containerId !== account.id || role.clientRole !== true
      || role.composite !== (name === 'manage-account')) refuse(stage, 'unsupported');
    roles.set(name, role);
    children.set(name, list(await admin.get(`roles-by-id/${encode(role.id)}/composites`), stage));
  }
  if (new Set([...roles.values()].map(role => role.id)).size !== roles.size
    || children.get('manage-account').length !== 1
    || !sameRole(children.get('manage-account')[0], roles.get('manage-account-links'))
    || children.get('manage-account-links').length || children.get('view-groups').length) refuse(stage, 'unsupported');
  const base = `clients/${encode(console.id)}`;
  const defaults = list(await admin.get(`${base}/default-client-scopes`), stage);
  const optional = list(await admin.get(`${base}/optional-client-scopes`), stage);
  const scopes = [...defaults, ...optional];
  if (new Set(scopes.map(scope => scope.id)).size !== scopes.length
    || scopes.some(scope => scope.protocol !== 'openid-connect')) refuse(stage, 'unsupported');
  const directMappers = list(await admin.get(`${base}/protocol-mappers/models`), stage);
  const scopeMappers = new Map();
  const allowances = new Map();
  for (const path of [base, ...scopes.map(scope => `client-scopes/${encode(scope.id)}`)]) {
    const mappings = await admin.get(`${path}/scope-mappings`);
    if (!mappings || typeof mappings !== 'object' || Array.isArray(mappings)
      || (mappings.realmMappings ?? []).length) refuse(stage, 'unsupported');
    for (const [name, mapping] of Object.entries(mappings.clientMappings ?? {})) {
      if (name !== 'account' || mapping.id !== account.id || mapping.client !== 'account') refuse(stage, 'unsupported');
      for (const role of list(mapping.mappings, stage))
        if (!allowedNames.some(name => sameRole(role, roles.get(name)))) refuse(stage, 'unsupported');
    }
    allowances.set(path, mappings);
    if (path !== base) scopeMappers.set(path, list(await admin.get(`${path}/protocol-mappers/models`), stage));
  }
  // A missing allowance is not repaired here. The supported baseline must
  // actually carry the approved capability to the fixed-account emitter.
  const permitted = [base, ...defaults.map(scope => `client-scopes/${encode(scope.id)}`)]
    .flatMap(path => allowances.get(path).clientMappings?.account?.mappings ?? []);
  if (!permitted.some(role => sameRole(role, roles.get('manage-account')))) refuse(stage, 'unsupported');
  return { account, console, roles, children, defaults, optional, directMappers, scopeMappers, allowances };
}

async function userAccess(admin, userId, accountId, stage) {
  const base = `users/${encode(userId)}/role-mappings/clients/${encode(accountId)}`;
  return { direct: list(await admin.get(base), stage),
    effective: list(await admin.get(`${base}/composite?briefRepresentation=true`), stage) };
}

function withGrant(mappings, account, role) {
  const result = copy(mappings);
  result.clientMappings ??= {};
  result.clientMappings.account ??= { id: account.id, client: 'account', mappings: [] };
  result.clientMappings.account.mappings.push(copy(role));
  return result;
}

export function exactCreatedAccountAccess(mappings, account, role) {
  const clients = mappings.clientMappings;
  return clients && Object.keys(clients).length === 1 && clients.account?.id === account.id
    && clients.account.client === 'account' && clients.account.mappings?.length === 1
    && sameRole(clients.account.mappings[0], role);
}

function canonicalMappings(mappings) {
  return { ...mappings,
    ...(mappings.realmMappings ? { realmMappings: ordered(mappings.realmMappings) } : {}),
    ...(mappings.clientMappings ? { clientMappings: Object.fromEntries(Object.entries(mappings.clientMappings)
      .map(([name, mapping]) => [name, { ...mapping, mappings: ordered(mapping.mappings) }])) } : {}) };
}

/** Owns only one missing mapper and four explicitly declared missing grants. */
export async function planAccountSelfService({ admin, desired, manifest, ownedUsers, add, preflightResponse }) {
  const declaredConsole = desired.clients.find(value => value.clientId === 'account-console');
  const canonical = declaredConsole?.protocolMappers?.find(mapper => mapper.name === mapperName);
  if (!canonical || canonical.protocolMapper !== 'oidc-usermodel-client-role-mapper') refuse('plan', 'unsupported');
  const before = await snapshot(admin, 'plan');
  const role = omit(before.roles.get('manage-account'), ['attributes']);
  const inheritedRoles = before.children.get('manage-account').map(value => omit(value, ['attributes']));
  const inheritedMappers = [...before.scopeMappers.values()].flat();
  const hasMapper = validateAccountMappers([...before.directMappers, ...inheritedMappers], canonical);
  const defaultMappers = before.defaults.flatMap(scope => before.scopeMappers.get(`client-scopes/${encode(scope.id)}`));
  // The console requests openid alone. An emitter present only in an optional
  // scope cannot carry that flow, and adding another would conflict when selected.
  const hasDefaultMapper = validateAccountMappers([...before.directMappers, ...defaultMappers], canonical);
  if (hasMapper && !hasDefaultMapper)
    refuse('plan', 'unsupported');
  if (!hasMapper && before.directMappers.some(mapper => mapper.name === mapperName)) refuse('plan', 'mapper_conflict');
  const expectedMappers = hasMapper ? before.directMappers : [...before.directMappers, copy(canonical)];
  if (expectedMappers.length > 200) refuse('plan', 'unsupported');
  preflightResponse(expectedMappers.map(mapper => ({ ...mapper, id: mapper.id ?? '0'.repeat(36) })));
  preflightResponse({ ...before.console, protocolMappers: expectedMappers.map(mapper => ({ ...mapper, id: mapper.id ?? '0'.repeat(36) })) });
  if (!hasMapper) add('create_account_mapper', async () => {
    const result = await admin.post(`clients/${encode(before.console.id)}/protocol-mappers/models`, copy(canonical));
    if (result.status !== 201 && result.status !== 409) refuse('apply', 'status');
    const actual = list(await admin.get(`clients/${encode(before.console.id)}/protocol-mappers/models`), 'apply');
    const found = actual.filter(mapper => mapper.name === canonical.name);
    if (found.length !== 1 || !equal(accountMapperRepresentation(omit(found[0], ['id'])), accountMapperRepresentation(canonical)))
      refuse('apply', 'mapper_conflict');
  }, canonical);
  const expected = new Map();
  for (const declaration of manifest.users) {
    const original = ownedUsers.get(declaration.username);
    if (!original) continue;
    const access = await userAccess(admin, original.user.id, before.account.id, 'plan');
    const mapped = original.roles.clientMappings?.account;
    if (mapped && (mapped.id !== before.account.id || mapped.client !== 'account')) refuse('plan', 'unsupported');
    if (!equal(ordered(mapped?.mappings ?? []), ordered(access.direct))) refuse('plan', 'identity_conflict');
    for (const actual of [...access.direct, ...access.effective])
      if ((actual.id === role.id || actual.name === role.name) && !sameRole(actual, role)) refuse('plan', 'unsupported');
    const missing = !access.effective.some(actual => sameRole(actual, role));
    if (missing && access.direct.some(actual => sameRole(actual, role))) refuse('plan', 'unsupported');
    if (!missing && !inheritedRoles.every(child => access.effective.some(actual => sameRole(actual, child))))
      refuse('plan', 'unsupported');
    const mappings = missing ? withGrant(original.roles, before.account, role) : copy(original.roles);
    const direct = missing ? [...access.direct, role] : access.direct;
    const effective = missing ? [...access.effective, role, ...inheritedRoles
      .filter(child => !access.effective.some(actual => actual.id === child.id))] : access.effective;
    list(direct, 'plan');
    list(effective, 'plan');
    for (const value of [mappings, direct, effective]) preflightResponse(value);
    expected.set(declaration.username, { mappings, direct, effective });
    if (missing) add('grant_account_self_service', async () => {
      const result = await admin.post(`users/${encode(original.user.id)}/role-mappings/clients/${encode(before.account.id)}`, [copy(role)]);
      if (result.status !== 204) refuse('apply', 'status');
    }, [role]);
  }
  return { account: before.account, role, verify: async afterUsers => {
    const check = condition => { if (!condition) refuse('readback', 'readback_conflict'); };
    const after = await snapshot(admin, 'readback');
    check(equal(omit(after.console, ['protocolMappers']), omit(before.console, ['protocolMappers'])));
    for (const key of ['account', 'roles', 'children', 'defaults', 'optional', 'scopeMappers', 'allowances']) check(equal(after[key], before[key]));
    check(equal(ordered(after.directMappers).map(mapper => accountMapperRepresentation(omit(mapper, ['id']))).sort(byName),
      expectedMappers.map(mapper => accountMapperRepresentation(omit(mapper, ['id']))).sort(byName)));
    for (const mapper of before.directMappers) check(after.directMappers.some(actual => equal(actual, mapper)));
    check(validateAccountMappers([...after.directMappers, ...after.scopeMappers.values()].flat(), canonical, 'readback'));
    for (const declaration of manifest.users) {
      const actual = afterUsers.get(declaration.username);
      check(Boolean(actual));
      const access = await userAccess(admin, actual.user.id, after.account.id, 'readback');
      check(access.effective.some(value => sameRole(value, role)));
      const original = expected.get(declaration.username);
      if (original) {
        check(equal(canonicalMappings(actual.roles), canonicalMappings(original.mappings)));
        check(equal(ordered(access.direct), ordered(original.direct)));
        check(equal(ordered(access.effective), ordered(original.effective)));
      } else check(exactCreatedAccountAccess(actual.roles, after.account, role)
        && equal(ordered(access.direct), ordered([role]))
        && equal(ordered(access.effective), ordered([role, ...inheritedRoles])));
    }
  } };
}

function byName(left, right) { return left.name.localeCompare(right.name); }
