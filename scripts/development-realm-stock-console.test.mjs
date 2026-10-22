import assert from 'node:assert/strict';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';
import { RealmError } from './development-realm-errors.mjs';
import { extractCapturedStockConsole } from './extract-development-realm-stock-console.mjs';
import { readStockConsoleGraph, qualifyStockConsoleGraph, stockConsoleDetachmentPath,
  extractStockConsoleCanonicalGraph } from './development-realm-stock-console.mjs';

import { stockConsoleProvider as fixture, stockConsoleBaseline as baseline, canonicalAccountMapper } from './development-realm-stock-console-test-fixture.mjs';

const clone = value => structuredClone(value);
const failure = stage => error => error instanceof RealmError && error.stage === stage && error.reason === 'unsupported';

test('the fixed historical graph binds eleven scopes and four roles and authorizes only three exact edges', async () => {
  const f = fixture(); const original = clone(f.capture);
  const plan = await f.qualify();
  assert.deepEqual(plan.detachments.map(value => `${value.category}/${value.name}`).sort(),
    ['default/roles', 'optional/microprofile-jwt', 'optional/offline_access']);
  for (const token of plan.detachments) assert.equal(stockConsoleDetachmentPath(token),
    `${f.base}/${token.category}-client-scopes/${token.scopeId}`);
  assert.equal(plan.remainingAssignments.defaults.length, 5);
  assert.equal(plan.remainingAssignments.optional.length, 3);
  assert.equal(plan.canonicalSha256, baseline.provenance.canonicalSha256);
  assert.equal(f.reads.filter(path => path.startsWith('roles-by-id/') && !path.endsWith('/composites')).length, 4);
  assert.deepEqual(f.capture, original);
});

test('different provider IDs produce the same independently bound canonical expectation', async () => {
  const first = fixture('one'); const second = fixture('two');
  assert.notEqual(first.account.id, second.account.id);
  assert.deepEqual(extractStockConsoleCanonicalGraph({ graph: await first.read() }),
    extractStockConsoleCanonicalGraph({ graph: await second.read() }));
});

test('every nonempty incompatible subset detaches only present edges without reattaching missing compatible edges', async () => {
  for (let mask = 1; mask < 8; mask++) {
    const f = fixture();
    f.assignments(mask & 1 ? ['roles', 'basic'] : ['basic'],
      [...(mask & 2 ? ['microprofile-jwt'] : []), ...(mask & 4 ? ['offline_access'] : [])]);
    const plan = await f.qualify();
    assert.equal(plan.detachments.length, [1, 2, 4].filter(bit => mask & bit).length);
    assert.deepEqual(plan.remainingAssignments.defaults.map(scope => scope.name), ['basic']);
    assert.deepEqual(plan.remainingAssignments.optional, []);
    f.detach(plan);
    await plan.verify({ admin: f.admin, existingAccountGraph: f.existing() });
  }
});

test('a graph with no incompatible edge cannot mint stock detachment authority', async () => {
  const f = fixture(); f.assignments(['basic'], ['phone']);
  await assert.rejects(f.qualify(), failure('plan'));
});

test('extra edges, wrong categories, duplicate IDs and summary substitutions refuse', async () => {
  for (const variant of ['extra', 'category', 'duplicate', 'summary_id', 'summary_name', 'summary_field']) {
    const f = fixture();
    if (variant === 'extra') f.assignments(['roles', 'custom'], []);
    if (variant === 'category') f.assignments(['microprofile-jwt'], ['roles']);
    const summaries = f.capture[`${f.base}/default-client-scopes`];
    if (variant === 'duplicate') summaries.push(clone(summaries[0]));
    if (variant === 'summary_id') summaries[0].id = 'aliased';
    if (variant === 'summary_name') summaries[0].name = 'aliased';
    if (variant === 'summary_field') summaries[0].retained = 'not-stock';
    await assert.rejects(f.qualify(), failure('plan'), variant);
  }
});

test('full scope identity and inventory bindings refuse independently substituted ID, name or protocol', async () => {
  for (const field of ['id', 'name', 'protocol']) {
    const f = fixture(); f.capture[f.scopePath('roles')][field] = 'aliased';
    await assert.rejects(f.qualify(), failure('plan'));
  }
});

test('lookalike scope fields, attributes, stored config and effective config remain significant', async () => {
  for (const variant of ['field', 'attribute', 'stored', 'effective']) {
    const f = fixture();
    f.changeScope('roles', value => {
      if (variant === 'field') value.unrecognized = false;
      if (variant === 'attribute') value.attributes.custom = '';
      if (variant === 'stored') value.protocolMappers[0].config.custom = 'false';
    });
    if (variant === 'effective') f.capture[`${f.scopePath('roles')}/protocol-mappers/models`][0].config.custom = 'false';
    await assert.rejects(f.qualify(), failure('plan'));
  }
});

test('stored and effective mapper DTOs are both retained rather than default-expanded or collapsed', async () => {
  const f = fixture(); const plan = await f.qualify();
  assert.deepEqual(plan.canonicalGraph.clients['account-console'].protocolMappers[0].config, {});
  assert.deepEqual(plan.canonicalGraph.directConsoleEffectiveMappers[0].config,
    { 'introspection.token.claim': 'true', 'access.token.claim': 'true' });
  f.capture[`${f.base}/protocol-mappers/models`][0].config = {};
  await assert.rejects(f.qualify(), failure('plan'));
});

test('mapper IDs bind both endpoint representations and cannot alias across owners', async () => {
  for (const variant of ['wrong_id', 'wrong_name', 'wrong_type', 'wrong_protocol', 'alias']) {
    const f = fixture(); const values = f.capture[`${f.scopePath('roles')}/protocol-mappers/models`];
    if (variant === 'wrong_id') values[0].id = 'aliased';
    if (variant === 'wrong_name') values[0].name = 'aliased';
    if (variant === 'wrong_type') values[0].protocolMapper = 'oidc-full-name-mapper';
    if (variant === 'wrong_protocol') values[0].protocol = 'saml';
    if (variant === 'alias') {
      const shared = f.capture[`${f.base}/protocol-mappers/models`][0].id;
      f.changeScope('roles', value => { value.protocolMappers[0].id = shared; }); values[0].id = shared;
    }
    await assert.rejects(f.qualify(), failure('plan'), variant);
  }
});

test('roles require independent name, ID, container, attributes and complete composite binding', async () => {
  for (const variant of ['named_id', 'by_id', 'container', 'attribute', 'extra_child', 'child_id', 'child_name']) {
    const f = fixture(); const path = `clients/${f.account.id}/roles/manage-account`;
    const named = f.capture[path]; const byId = `roles-by-id/${named.id}`;
    if (variant === 'named_id') named.id = f.capture[`clients/${f.account.id}/roles/manage-account-links`].id;
    if (variant === 'by_id') f.capture[byId].id = 'aliased';
    if (variant === 'container') named.containerId = f.console.id;
    if (variant === 'attribute') { named.attributes.retained = ['changed']; f.capture[byId] = clone(named); }
    if (variant === 'extra_child') f.capture[`${byId}/composites`].push({ id: 'unknown', name: 'unknown' });
    if (variant === 'child_id') f.capture[`${byId}/composites`][0].id = 'aliased';
    if (variant === 'child_name') f.capture[`${byId}/composites`][0].name = 'aliased';
    await assert.rejects(f.qualify(), failure('plan'), variant);
  }
});

test('complete client fields and account assignment bindings reject stock lookalikes', async () => {
  for (const variant of ['account_field', 'console_field', 'access', 'account_summary', 'account_edge']) {
    const f = fixture();
    if (variant === 'account_field') f.capture[`clients/${f.account.id}`].unknown = false;
    if (variant === 'console_field') f.capture[f.base].unknown = false;
    if (variant === 'access') f.capture[f.base].access.configure = false;
    if (variant === 'account_summary') f.capture[`clients/${f.account.id}/default-client-scopes`][0].id = 'aliased';
    if (variant === 'account_edge') f.capture[`clients/${f.account.id}`].defaultClientScopes.pop();
    await assert.rejects(f.qualify(), failure('plan'), variant);
  }
});

test('unreferenced client, scope and role customization is outside the qualified closure', async () => {
  const f = fixture();
  f.capture.clients.push({ id: 'unrelated-client', clientId: 'unrelated', secret: 'synthetic-private' });
  f.capture['client-scopes'].push({ id: 'unrelated-scope', name: 'unrelated', protocol: 'saml', attributes: { custom: true } });
  f.capture.roles = [{ id: 'unrelated-role', name: 'custom-role', attributes: { arbitrary: ['retained'] } }];
  const original = clone(f.capture);
  await f.qualify();
  assert.deepEqual(f.capture, original);
  assert.equal(f.reads.includes('roles'), false);
});

test('broader or substituted direct and scope allowances cannot qualify by historical names', async () => {
  for (const variant of ['direct_unknown', 'direct_role', 'scope_client', 'scope_realm', 'scope_unknown']) {
    const f = fixture(); const mapping = f.capture[`${f.base}/scope-mappings`];
    if (variant === 'direct_unknown') mapping.extra = [];
    if (variant === 'direct_role') mapping.clientMappings.account.mappings[0].id = 'aliased';
    const path = `${f.scopePath('phone')}/scope-mappings`;
    if (variant === 'scope_client') f.capture[path] = { clientMappings: { other: { id: 'other', client: 'other', mappings: [] } } };
    if (variant === 'scope_realm') f.capture[path] = { realmMappings: [clone(f.capture['roles/offline_access'])] };
    if (variant === 'scope_unknown') f.capture[path] = { custom: false };
    await assert.rejects(f.qualify(), failure('plan'), variant);
  }
});

test('one separately approved canonical mapper is preserved with its existing provider identity', async () => {
  const f = fixture(); const mapper = f.addApproved(); const plan = await f.qualify();
  assert.deepEqual(plan.approvedMapperDelta, { stored: mapper, effective: mapper });
  f.detach(plan);
  await plan.verify({ admin: f.admin, existingAccountGraph: f.existing() });
});

test('unknown, renamed, duplicate and modified approved mapper lookalikes refuse stock fallback', async () => {
  for (const variant of ['renamed', 'duplicate', 'config', 'field', 'stored', 'id']) {
    const f = fixture(); f.addApproved(variant === 'renamed' ? 'custom-name' : undefined);
    const effective = f.capture[`${f.base}/protocol-mappers/models`];
    if (variant === 'duplicate') f.addApproved();
    if (variant === 'config') effective.at(-1).config['userinfo.token.claim'] = 'true';
    if (variant === 'field') effective.at(-1).unknown = false;
    if (variant === 'stored') f.capture[f.base].protocolMappers.at(-1).config.unrecognized = '';
    if (variant === 'id') effective.at(-1).id = 'different';
    await assert.rejects(f.qualify(), failure('plan'), variant);
  }
});

test('readback accepts exactly the authorized edge delta and an independently bound approved mapper addition', async () => {
  const f = fixture(); const plan = await f.qualify(); f.detach(plan); f.addApproved();
  await plan.verify({ admin: f.admin, existingAccountGraph: f.existing() });
  await assert.rejects(f.qualify(), failure('plan'));
});

test('readback retains existing mapper IDs even when all semantic mapper fields still match', async () => {
  const f = fixture(); const plan = await f.qualify(); f.detach(plan);
  f.changeScope('roles', value => { value.protocolMappers[0].id = 'replaced-id'; });
  f.capture[`${f.scopePath('roles')}/protocol-mappers/models`][0].id = 'replaced-id';
  await assert.rejects(plan.verify({ admin: f.admin, existingAccountGraph: f.existing() }),
    error => error.stage === 'readback' && error.reason === 'readback_conflict');
});

test('readback refuses reattached unsafe edges, removed compatible edges and changed account edges', async () => {
  for (const variant of ['reattach', 'remove', 'account']) {
    const f = fixture(); const plan = await f.qualify(); f.detach(plan);
    if (variant === 'reattach') f.assignments([...f.capture[f.base].defaultClientScopes, 'roles'], f.capture[f.base].optionalClientScopes);
    if (variant === 'remove') f.assignments(f.capture[f.base].defaultClientScopes.filter(name => name !== 'basic'), f.capture[f.base].optionalClientScopes);
    if (variant === 'account') {
      f.capture[`clients/${f.account.id}`].optionalClientScopes.pop();
      f.capture[`clients/${f.account.id}/optional-client-scopes`].pop();
    }
    await assert.rejects(plan.verify({ admin: f.admin, existingAccountGraph: f.existing() }), error => error.stage === 'readback');
  }
});

test('an existing approved mapper cannot be replaced, removed or modified during readback', async () => {
  for (const variant of ['id', 'removed', 'config']) {
    const f = fixture(); f.addApproved(); const plan = await f.qualify(); f.detach(plan);
    for (const values of [f.capture[f.base].protocolMappers, f.capture[`${f.base}/protocol-mappers/models`]]) {
      if (variant === 'id') values.at(-1).id = 'replaced';
      if (variant === 'removed') values.pop();
      if (variant === 'config') values.at(-1).config.unrecognized = '';
    }
    await assert.rejects(plan.verify({ admin: f.admin, existingAccountGraph: f.existing() }), error => error.stage === 'readback');
  }
});

test('transport and arbitrary read exceptions propagate without stock fallback or replay', async () => {
  for (const error of [new RealmError('plan', 'transport'), new Error('synthetic-private-canary')]) {
    const f = fixture(); let calls = 0;
    f.admin.get = async () => { calls++; throw error; };
    await assert.rejects(f.read(), actual => actual === error);
    assert.equal(calls, 1);
  }
});

test('all read collections retain the existing 200-entry bound', async () => {
  for (const variant of ['inventory', 'mappers', 'composites', 'assignments']) {
    const f = fixture(); const values = Array.from({ length: 201 }, (_, index) => ({ id: `extra-${index}`, name: `extra-${index}` }));
    if (variant === 'inventory') {
      f.capture['client-scopes'].push(...values.slice(0, 189));
      await f.qualify();
      f.capture['client-scopes'].push(values[189]);
    }
    if (variant === 'mappers') f.capture[`${f.scopePath('roles')}/protocol-mappers/models`] = values;
    if (variant === 'composites') {
      const role = f.capture[`clients/${f.account.id}/roles/manage-account`]; f.capture[`roles-by-id/${role.id}/composites`] = values;
    }
    if (variant === 'assignments') f.capture[`${f.base}/default-client-scopes`] = values;
    await assert.rejects(f.qualify(), failure('plan'), variant);
  }
});

test('capture extraction requires the previously missing independent assignment and role-name reads', async () => {
  for (const variant of ['account_assignment', 'role_name']) {
    const f = fixture();
    delete f.capture[variant === 'account_assignment' ? `clients/${f.account.id}/optional-client-scopes` : 'roles/offline_access'];
    await assert.rejects(extractCapturedStockConsole(f.capture), error => error.stage === 'plan' && error.reason === 'response');
  }
});

test('offline extraction compares observed data to the fixed expectation without tokens or current desired data', async () => {
  const f = fixture(); const output = await extractCapturedStockConsole(f.capture);
  assert.deepEqual(output.canonical, baseline.canonical);
  assert.equal(output.canonicalSha256, baseline.provenance.canonicalSha256);
  assert.equal(output.bytes.includes('provider-'), false);
  const graph = await f.read();
  assert.throws(() => stockConsoleDetachmentPath(extractStockConsoleCanonicalGraph({ graph })), failure('apply'));
});

test('detachment tokens cannot be forged, cloned or retargeted through a mutated returned graph', async () => {
  const f = fixture(); const graph = await f.read();
  graph.console.id = 'foreign-console';
  const plan = qualifyStockConsoleGraph({ graph, canonicalAccountMapper });
  assert.ok(plan.detachments.every(value => value.consoleId === f.console.id));
  const token = plan.detachments[0];
  assert.throws(() => stockConsoleDetachmentPath(clone(token)), failure('apply'));
  assert.throws(() => stockConsoleDetachmentPath(null), failure('apply'));
  assert.throws(() => qualifyStockConsoleGraph({ graph: clone(graph), canonicalAccountMapper }), failure('plan'));
  assert.equal(Object.isFrozen(token), true);
});

test('the offline CLI checks a private capture and cannot overwrite the committed expectation', async t => {
  const temporary = await mkdtemp(join(tmpdir(), 'learnstack-stock-extract-'));
  t.after(() => rm(temporary, { recursive: true, force: true }));
  const input = join(temporary, 'capture.json'); await writeFile(input, JSON.stringify(fixture().capture), { mode: 0o600 });
  const script = 'scripts/extract-development-realm-stock-console.mjs';
  const checked = spawnSync(process.execPath, [script, '--check', input], { encoding: 'utf8' });
  assert.equal(checked.status, 0);
  assert.equal(checked.stdout, `stock-console: fixed expectation matched; sha256=${baseline.provenance.canonicalSha256}\n`);
  const refused = spawnSync(process.execPath, [script, '--candidate', input, 'scripts/development-realm-stock-console-baseline.json'], { encoding: 'utf8' });
  assert.equal(refused.status, 1);
  assert.equal(refused.stderr, 'realm: preflight/configuration\n');
  const malformed = fixture().capture;
  malformed['roles/offline_access'].name = 'synthetic-private-canary';
  await writeFile(input, JSON.stringify(malformed), { mode: 0o600 });
  const privateFailure = spawnSync(process.execPath, [script, '--check', input], { encoding: 'utf8' });
  assert.equal(privateFailure.status, 1);
  assert.equal(privateFailure.stdout, '');
  assert.equal(privateFailure.stderr, 'realm: plan/unsupported\n');
});
