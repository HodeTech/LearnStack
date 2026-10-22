import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { isDeepStrictEqual as equal } from 'node:util';
import { refuse } from './development-realm-errors.mjs';
import { accountMapperRepresentation, validateAccountMappers } from './development-realm-mappers.mjs';

const baseline = JSON.parse(await readFile(new URL('./development-realm-stock-console-baseline.json', import.meta.url), 'utf8'));
const expected = baseline.canonical;
const encode = encodeURIComponent;
const copy = value => structuredClone(value);
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const omit = (value, keys) => Object.fromEntries(Object.entries(value).filter(([key]) => !keys.includes(key)));
const byName = (left, right) => left.name.localeCompare(right.name);
const byId = (left, right) => left.id.localeCompare(right.id);
const namedMapper = 'learnstack-account-self-service-roles';
const detachmentPaths = new WeakMap();
const readGraphs = new WeakMap();
const categories = ['default', 'optional'];
const incompatible = { roles: 'default', 'microprofile-jwt': 'optional', offline_access: 'optional' };

function check(condition, stage, reason = 'unsupported') {
  if (!condition) refuse(stage, reason);
}

function record(value, stage) {
  check(object(value), stage);
  return value;
}

function id(value, stage) {
  check(typeof value === 'string' && value.length > 0, stage);
  return value;
}

function list(value, stage, named = false) {
  check(Array.isArray(value) && value.length <= 200, stage);
  const ids = new Set();
  const names = new Set();
  for (const item of value) {
    record(item, stage);
    id(item.id, stage);
    check(!ids.has(item.id), stage);
    ids.add(item.id);
    if (named) {
      id(item.name, stage);
      check(!names.has(item.name), stage);
      names.add(item.name);
    }
  }
  return value;
}

function stable(value) {
  if (Array.isArray(value)) return value.map(stable);
  if (!object(value)) return value;
  return Object.fromEntries(Object.keys(value).sort().map(key => [key, stable(value[key])]));
}

function hash(value) {
  return createHash('sha256').update(JSON.stringify(stable(value)) + '\n').digest('hex');
}

function assignmentNames(client, category, summaries, stage) {
  const names = client[`${category}ClientScopes`];
  check(Array.isArray(names) && names.length <= 200 && names.every(name => typeof name === 'string' && name)
    && new Set(names).size === names.length, stage);
  check(equal([...names].sort(), summaries.map(scope => scope.name).sort()), stage);
  check(summaries.every(scope => equal(Object.keys(scope).sort(), ['id', 'name'])), stage);
}

function assignments(defaults, optional, stage) {
  list(defaults, stage, true);
  list(optional, stage, true);
  const combined = [...defaults, ...optional];
  check(new Set(combined.map(scope => scope.id)).size === combined.length
    && new Set(combined.map(scope => scope.name)).size === combined.length, stage);
  return { default: defaults, optional };
}

function identityFields(mapper) { return omit(mapper, ['config']); }

function bindMappers(stored, effective, owner, bindings, stage) {
  list(stored, stage, true);
  list(effective, stage, true);
  check(equal([...stored].sort(byId).map(identityFields), [...effective].sort(byId).map(identityFields)), stage);
  for (const mapper of stored) {
    check(mapper.protocol === 'openid-connect' && typeof mapper.protocolMapper === 'string'
      && object(mapper.config), stage);
    const identity = `${owner}/${mapper.name}`;
    check(!bindings.has(mapper.id) || bindings.get(mapper.id) === identity, stage);
    bindings.set(mapper.id, identity);
  }
  for (const mapper of effective) check(object(mapper.config), stage);
}

function requireClient(value, name, stage) {
  record(value, stage);
  id(value.id, stage);
  check(value.clientId === name && value.protocol === 'openid-connect', stage);
}

/** Read-only closure of the eleven pinned definitions and their referenced roles. */
export async function readStockConsoleGraph({ admin, existingAccountGraph, stage = 'plan' }) {
  check(admin && typeof admin.get === 'function', stage);
  const before = record(existingAccountGraph, stage);
  for (const key of ['scopeDefinitions', 'scopeMappers', 'allowances'])
    check(before[key] === undefined || before[key] instanceof Map, stage);
  requireClient(before.account, 'account', stage);
  requireClient(before.console, 'account-console', stage);
  check(before.account.id !== before.console.id, stage);
  const realm = record(await admin.get(''), stage);
  check(realm.realm === 'learnstack', stage);
  id(realm.id, stage);
  const consoleAssignments = assignments(before.defaults, before.optional, stage);
  const accountAssignments = assignments(
    await admin.get(`clients/${encode(before.account.id)}/default-client-scopes`),
    await admin.get(`clients/${encode(before.account.id)}/optional-client-scopes`), stage);
  for (const category of categories) {
    assignmentNames(before.console, category, consoleAssignments[category], stage);
    assignmentNames(before.account, category, accountAssignments[category], stage);
    const allowed = new Set(expected.assignments[category].map(scope => scope.name));
    check(consoleAssignments[category].every(scope => allowed.has(scope.name)), stage);
    check(equal(accountAssignments[category].map(scope => scope.name).sort(), [...allowed].sort()), stage);
  }
  const inventory = list(await admin.get('client-scopes'), stage, true);
  const byScopeName = new Map(inventory.map(scope => [scope.name, scope]));
  const scopes = new Map();
  const mapperBindings = new Map();
  const consolePath = `clients/${encode(before.console.id)}`;
  const directMappers = list(before.directMappers, stage, true);
  bindMappers(before.console.protocolMappers ?? [], directMappers, 'client/account-console', mapperBindings, stage);
  const directMappings = record(before.allowances?.get(consolePath), stage);
  for (const summary of [...accountAssignments.default, ...accountAssignments.optional]) {
    const indexed = byScopeName.get(summary.name);
    check(indexed?.id === summary.id, stage);
    for (const assigned of [...consoleAssignments.default, ...consoleAssignments.optional].filter(value => value.name === summary.name))
      check(assigned.id === summary.id, stage);
    const path = `client-scopes/${encode(summary.id)}`;
    const definition = record(before.scopeDefinitions?.get(summary.id) ?? await admin.get(path), stage);
    check(definition.id === summary.id && definition.name === summary.name && definition.protocol === 'openid-connect'
      && equal(definition, indexed), stage);
    const effectiveMappers = before.scopeMappers?.get(path) ?? await admin.get(`${path}/protocol-mappers/models`);
    bindMappers(definition.protocolMappers ?? [], effectiveMappers, `scope/${summary.name}`, mapperBindings, stage);
    const allowances = record(before.allowances?.get(path) ?? await admin.get(`${path}/scope-mappings`), stage);
    scopes.set(summary.name, { definition: copy(definition), effectiveMappers: copy(effectiveMappers), allowances: copy(allowances) });
  }
  const roles = new Map();
  const roleIds = new Set();
  const pending = [
    { name: 'offline_access', clientRole: false },
    ...['manage-account', 'manage-account-links', 'view-groups'].map(name => ({ name, clientRole: true })),
  ];
  for (const reference of pending) {
    const path = reference.clientRole ? `clients/${encode(before.account.id)}/roles/${encode(reference.name)}` : `roles/${encode(reference.name)}`;
    const named = record(await admin.get(path), stage);
    id(named.id, stage);
    check(named.name === reference.name && named.clientRole === reference.clientRole
      && named.containerId === (reference.clientRole ? before.account.id : realm.id) && !roleIds.has(named.id), stage);
    const definition = record(await admin.get(`roles-by-id/${encode(named.id)}`), stage);
    check(equal(definition, named), stage);
    const composites = list(await admin.get(`roles-by-id/${encode(named.id)}/composites`), stage, true);
    check(typeof definition.composite === 'boolean' && definition.composite === (composites.length > 0), stage);
    roleIds.add(named.id);
    roles.set(`${reference.clientRole ? 'client/account' : 'realm'}/${reference.name}`,
      { definition: copy(definition), composites: copy(composites) });
  }
  const graph = { realmId: realm.id, account: copy(before.account), console: copy(before.console),
    accountAssignments: copy(accountAssignments), consoleAssignments: copy(consoleAssignments),
    directMappers: copy(directMappers), directMappings: copy(directMappings), scopes, roles };
  readGraphs.set(graph, copy(graph));
  return graph;
}

function canonicalize(graph, canonicalAccountMapper, stage) {
  const roleById = new Map([...graph.roles.values()].map(role => [role.definition.id, role.definition]));
  const clientIds = new Map([[graph.account.id, 'account'], [graph.console.id, 'account-console']]);
  const role = value => {
    record(value, stage);
    const authoritative = roleById.get(value.id);
    check(Boolean(authoritative), stage);
    check(equal(value, authoritative) || equal(value, omit(authoritative, ['attributes'])), stage);
    const result = copy(value);
    const owner = authoritative.clientRole ? `client/${clientIds.get(authoritative.containerId)}` : 'realm';
    check(!authoritative.clientRole || clientIds.get(authoritative.containerId) === 'account', stage);
    result.id = `@role/${owner}/${authoritative.name}`;
    result.containerId = authoritative.clientRole ? `@client/${clientIds.get(authoritative.containerId)}` : '@realm/learnstack';
    return result;
  };
  const mapper = (value, owner) => ({ ...copy(value), id: `@mapper/${owner}/${value.name}` });
  const mappers = (values, owner) => values.map(value => mapper(value, owner)).sort(byName);
  const mappings = value => {
    const result = copy(value);
    if (Object.hasOwn(value, 'realmMappings')) result.realmMappings = list(value.realmMappings, stage, true).map(role).sort(byId);
    if (Object.hasOwn(value, 'clientMappings')) {
      record(value.clientMappings, stage);
      check(Object.keys(value.clientMappings).length <= 200, stage);
      result.clientMappings = Object.fromEntries(Object.entries(value.clientMappings).map(([name, mapping]) => {
        record(mapping, stage);
        check(name === 'account' && mapping.client === name && mapping.id === graph.account.id, stage);
        return [name, { ...copy(mapping), id: '@client/account', mappings: list(mapping.mappings, stage, true).map(role).sort(byId) }];
      }));
    }
    return result;
  };
  const approvedStored = (graph.console.protocolMappers ?? []).filter(value => value.name === namedMapper);
  const approvedEffective = graph.directMappers.filter(value => value.name === namedMapper);
  check(approvedStored.length <= 1 && approvedStored.length === approvedEffective.length, stage);
  let approvedMapperDelta = null;
  if (approvedStored.length) {
    check(canonicalAccountMapper?.name === namedMapper, stage);
    check(approvedStored[0].id === approvedEffective[0].id, stage);
    for (const value of [...approvedStored, ...approvedEffective])
      check(equal(accountMapperRepresentation(omit(value, ['id'])), accountMapperRepresentation(canonicalAccountMapper)), stage);
    approvedMapperDelta = { stored: copy(approvedStored[0]), effective: copy(approvedEffective[0]) };
  }
  const result = { schema: 1, providerVersion: '26.6.2', clients: {}, assignments: {}, scopes: {}, directConsoleMappings: {}, roles: {} };
  for (const [name, source] of [['account', graph.account], ['account-console', graph.console]]) {
    const current = { ...copy(source), id: `@client/${name}` };
    for (const category of categories) current[`${category}ClientScopes`] = [...source[`${category}ClientScopes`]].sort();
    if (Object.hasOwn(source, 'protocolMappers'))
      current.protocolMappers = mappers(source.protocolMappers.filter(value => name !== 'account-console' || value.name !== namedMapper), `client/${name}`);
    result.clients[name] = current;
  }
  result.directConsoleEffectiveMappers = mappers(graph.directMappers.filter(value => value.name !== namedMapper), 'client/account-console');
  for (const category of categories) result.assignments[category] = graph.consoleAssignments[category]
    .map(value => ({ ...copy(value), id: `@scope/${value.name}` })).sort(byName);
  for (const [name, scope] of graph.scopes) {
    const definition = { ...copy(scope.definition), id: `@scope/${name}` };
    if (Object.hasOwn(definition, 'protocolMappers')) definition.protocolMappers = mappers(scope.definition.protocolMappers, `scope/${name}`);
    result.scopes[name] = { definition, effectiveMappers: mappers(scope.effectiveMappers, `scope/${name}`), allowances: mappings(scope.allowances) };
  }
  result.directConsoleMappings = mappings(graph.directMappings);
  for (const value of graph.roles.values()) {
    const definition = role(value.definition);
    result.roles[definition.id] = { definition, composites: value.composites.map(role).sort(byId) };
  }
  const expectedProjection = copy(expected);
  for (const category of categories) {
    const allowed = new Map(expected.assignments[category].map(value => [value.name, value]));
    for (const value of result.assignments[category]) check(equal(value, allowed.get(value.name)), stage);
    expectedProjection.assignments[category] = copy(result.assignments[category]);
    expectedProjection.clients['account-console'][`${category}ClientScopes`] = result.assignments[category].map(value => value.name).sort();
  }
  check(equal(result, expectedProjection), stage);
  return { canonicalGraph: result, approvedMapperDelta };
}

function snapshot(graph, approved) {
  const sortedMappings = value => ({ ...copy(value),
    ...(Object.hasOwn(value, 'realmMappings') ? { realmMappings: [...value.realmMappings].sort(byId) } : {}),
    ...(Object.hasOwn(value, 'clientMappings') ? { clientMappings: Object.fromEntries(Object.entries(value.clientMappings)
      .map(([name, mapping]) => [name, { ...copy(mapping), mappings: [...mapping.mappings].sort(byId) }])) } : {}) });
  const client = value => ({ ...copy(value),
    defaultClientScopes: [...value.defaultClientScopes].sort(), optionalClientScopes: [...value.optionalClientScopes].sort(),
    ...(Object.hasOwn(value, 'protocolMappers') ? { protocolMappers: value.protocolMappers.filter(mapper => mapper.id !== approved?.stored.id).sort(byId) } : {}) });
  return { realmId: graph.realmId, account: client(graph.account), console: client(graph.console),
    accountAssignments: Object.fromEntries(categories.map(category => [category, [...graph.accountAssignments[category]].sort(byId)])),
    consoleAssignments: Object.fromEntries(categories.map(category => [category, [...graph.consoleAssignments[category]].sort(byId)])),
    directMappers: graph.directMappers.filter(mapper => mapper.id !== approved?.effective.id).sort(byId),
    directMappings: sortedMappings(graph.directMappings),
    scopes: [...graph.scopes].sort(([left], [right]) => left.localeCompare(right)).map(([name, scope]) => [name, {
      definition: { ...copy(scope.definition), ...(Object.hasOwn(scope.definition, 'protocolMappers')
        ? { protocolMappers: [...scope.definition.protocolMappers].sort(byId) } : {}) },
      effectiveMappers: [...scope.effectiveMappers].sort(byId), allowances: sortedMappings(scope.allowances) }]),
    roles: [...graph.roles].sort(([left], [right]) => left.localeCompare(right)).map(([name, role]) => [name,
      { definition: role.definition, composites: [...role.composites].sort(byId) }]) };
}

/** The caller selects this only after a successfully read incompatible graph. */
export function qualifyStockConsoleGraph({ graph, canonicalAccountMapper, stage = 'plan' }) {
  check(object(graph) && readGraphs.has(graph), stage);
  graph = readGraphs.get(graph);
  check(canonicalAccountMapper?.name === namedMapper, stage);
  canonicalAccountMapper = copy(canonicalAccountMapper);
  check(hash(expected) === baseline.provenance.canonicalSha256, stage);
  const normalized = canonicalize(graph, canonicalAccountMapper, stage);
  const detachments = [];
  const remainingAssignments = { defaults: [], optional: [] };
  for (const category of categories) for (const scope of graph.consoleAssignments[category]) {
    if (incompatible[scope.name] === category) {
      const binding = Object.freeze({ category, name: scope.name, consoleId: graph.console.id, scopeId: scope.id });
      detachmentPaths.set(binding, `clients/${encode(graph.console.id)}/${category}-client-scopes/${encode(scope.id)}`);
      detachments.push(binding);
    } else remainingAssignments[category === 'default' ? 'defaults' : 'optional'].push(copy(scope));
  }
  check(detachments.length > 0 && detachments.length <= 3, stage);
  const retained = [...remainingAssignments.defaults, ...remainingAssignments.optional];
  validateAccountMappers([...graph.directMappers, ...retained.flatMap(scope => graph.scopes.get(scope.name).effectiveMappers)], canonicalAccountMapper, stage);
  const expectedSnapshot = snapshot(graph, normalized.approvedMapperDelta);
  expectedSnapshot.consoleAssignments = { default: copy(remainingAssignments.defaults).sort(byId), optional: copy(remainingAssignments.optional).sort(byId) };
  expectedSnapshot.console.defaultClientScopes = remainingAssignments.defaults.map(scope => scope.name).sort();
  expectedSnapshot.console.optionalClientScopes = remainingAssignments.optional.map(scope => scope.name).sort();
  return { detachments: Object.freeze(detachments), remainingAssignments, approvedMapperDelta: copy(normalized.approvedMapperDelta),
    canonicalSha256: baseline.provenance.canonicalSha256, canonicalGraph: normalized.canonicalGraph,
    verify: async ({ admin, existingAccountGraph }) => {
      const after = await readStockConsoleGraph({ admin, existingAccountGraph, stage: 'readback' });
      const current = canonicalize(after, canonicalAccountMapper, 'readback');
      if (normalized.approvedMapperDelta) check(equal(current.approvedMapperDelta, normalized.approvedMapperDelta), 'readback', 'readback_conflict');
      check(equal(snapshot(after, current.approvedMapperDelta), expectedSnapshot), 'readback', 'readback_conflict');
    } };
}

/** Only tokens minted by successful qualification can authorize a DELETE path. */
export function stockConsoleDetachmentPath(binding, stage = 'apply') {
  check(object(binding) && detachmentPaths.has(binding), stage);
  return detachmentPaths.get(binding);
}

/** Offline fixed-expectation comparison; it cannot mint detachment authority. */
export function extractStockConsoleCanonicalGraph({ graph, stage = 'plan' }) {
  check(object(graph) && readGraphs.has(graph), stage);
  graph = readGraphs.get(graph);
  check(hash(expected) === baseline.provenance.canonicalSha256, stage);
  const result = canonicalize(graph, undefined, stage);
  for (const category of categories)
    check(equal(result.canonicalGraph.assignments[category], expected.assignments[category]), stage);
  return result.canonicalGraph;
}
