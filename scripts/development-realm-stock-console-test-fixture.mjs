import { readFile } from 'node:fs/promises';
import { RealmError } from './development-realm-errors.mjs';
import { readStockConsoleGraph, qualifyStockConsoleGraph } from './development-realm-stock-console.mjs';

// Test-only fixed historical observations; production entry points never import this module.
export const stockConsoleBaseline = JSON.parse(await readFile(new URL('./development-realm-stock-console-baseline.json', import.meta.url), 'utf8'));
const baseline = stockConsoleBaseline;
const declaration = JSON.parse(await readFile(new URL('../infra/keycloak/realms/learnstack.json', import.meta.url), 'utf8'));
// The current export supplies only the separately approved mapper declaration, never stock expectations.
export const canonicalAccountMapper = declaration.clients.find(client => client.clientId === 'account-console').protocolMappers
  .find(mapper => mapper.name === 'learnstack-account-self-service-roles');
const clone = value => structuredClone(value);

export function stockConsoleProvider(prefix = 'provider') {
  let serial = 0;
  const ids = new Map();
  const materialize = value => {
    if (typeof value === 'string' && value.startsWith('@')) {
      if (!ids.has(value)) ids.set(value, `${prefix}-${serial += 1}`);
      return ids.get(value);
    }
    if (Array.isArray(value)) return value.map(materialize);
    if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, materialize(item)]));
    return value;
  };
  const data = materialize(clone(baseline.canonical));
  const account = data.clients.account;
  const console = data.clients['account-console'];
  const definitions = Object.values(data.scopes).map(value => value.definition);
  const scopeByName = new Map(definitions.map(scope => [scope.name, scope]));
  const capture = { '': { id: materialize('@realm/learnstack'), realm: 'learnstack' },
    clients: [clone(account), clone(console)], 'client-scopes': clone(definitions) };
  const scopePath = name => `client-scopes/${scopeByName.get(name).id}`;
  for (const client of [account, console]) {
    const path = `clients/${client.id}`;
    capture[path] = clone(client);
    for (const kind of ['default', 'optional']) capture[`${path}/${kind}-client-scopes`] = client[`${kind}ClientScopes`]
      .map(name => ({ id: scopeByName.get(name).id, name }));
  }
  const base = `clients/${console.id}`;
  capture[`${base}/protocol-mappers/models`] = clone(data.directConsoleEffectiveMappers);
  capture[`${base}/scope-mappings`] = clone(data.directConsoleMappings);
  for (const [name, value] of Object.entries(data.scopes)) {
    const path = scopePath(name);
    capture[path] = clone(value.definition);
    capture[`${path}/protocol-mappers/models`] = clone(value.effectiveMappers);
    capture[`${path}/scope-mappings`] = clone(value.allowances);
  }
  for (const value of Object.values(data.roles)) {
    const role = value.definition;
    const path = role.clientRole ? `clients/${account.id}/roles/${encodeURIComponent(role.name)}` : `roles/${encodeURIComponent(role.name)}`;
    capture[path] = clone(role);
    capture[`roles-by-id/${role.id}`] = clone(role);
    capture[`roles-by-id/${role.id}/composites`] = clone(value.composites);
  }
  const reads = [];
  const admin = { get: async path => {
    reads.push(path);
    if (!Object.hasOwn(capture, path)) throw new RealmError('plan', 'response');
    return clone(capture[path]);
  } };
  const existing = () => ({ account: clone(capture[`clients/${account.id}`]), console: clone(capture[base]),
    defaults: clone(capture[`${base}/default-client-scopes`]), optional: clone(capture[`${base}/optional-client-scopes`]),
    directMappers: clone(capture[`${base}/protocol-mappers/models`]), allowances: new Map([[base, clone(capture[`${base}/scope-mappings`])]]) });
  const read = () => readStockConsoleGraph({ admin, existingAccountGraph: existing() });
  const qualify = async () => qualifyStockConsoleGraph({ graph: await read(), canonicalAccountMapper });
  const assignments = (defaults, optional) => {
    for (const [kind, names] of [['default', defaults], ['optional', optional]]) {
      capture[base][`${kind}ClientScopes`] = [...names];
      capture[`${base}/${kind}-client-scopes`] = names.map(name => ({ id: scopeByName.get(name)?.id ?? `unknown-${name}`, name }));
    }
  };
  const detach = plan => {
    for (const binding of plan.detachments) {
      const path = `${base}/${binding.category}-client-scopes`;
      capture[path] = capture[path].filter(scope => scope.id !== binding.scopeId);
      capture[base][`${binding.category}ClientScopes`] = capture[base][`${binding.category}ClientScopes`].filter(name => name !== binding.name);
    }
  };
  const addApproved = (name = canonicalAccountMapper.name) => {
    const mapper = { ...clone(canonicalAccountMapper), id: 'approved-provider-mapper', name };
    delete mapper.config['usermodel.clientRoleMapping.rolePrefix'];
    capture[base].protocolMappers.push(clone(mapper));
    capture[`${base}/protocol-mappers/models`].push(clone(mapper));
    return mapper;
  };
  const changeScope = (name, mutate) => {
    mutate(capture[scopePath(name)]);
    capture['client-scopes'] = capture['client-scopes'].map(scope => scope.name === name ? clone(capture[scopePath(name)]) : scope);
  };
  return { account, console, base, capture, scopePath, reads, admin, existing, read, qualify, assignments, detach, addApproved, changeScope };
}
