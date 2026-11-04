import test from 'node:test';
import assert from 'node:assert/strict';
import { seedAdministrationOriginTenantIds, adoptSeedAdministrationOrigins } from './seed-admin-origins.mjs';
import { realmFailureNotice } from './development-realm-errors.mjs';

const first = '01930000-0000-7000-8000-000000000001';
const second = '01930000-0000-7000-8000-000000000002';
const result = (ids, qualified = []) => 'seed-result: ' + JSON.stringify({ version: 2, freshTenantIds: ids, qualifiedOriginTenantIds: qualified });

test('only this successful owned run supplies bounded fresh and already-qualified origin selections', () => {
  assert.deepEqual(seedAdministrationOriginTenantIds('unrelated progress\n' + result([first, second]) + '\n'), [first, second]);
  assert.equal(Object.isFrozen(seedAdministrationOriginTenantIds(result([]))), true);
  assert.deepEqual(seedAdministrationOriginTenantIds(result([])), []);
  assert.deepEqual(seedAdministrationOriginTenantIds(result([], [second])), [second]);
  assert.deepEqual(seedAdministrationOriginTenantIds(result([first], [second])), [first, second]);
  assert.deepEqual(seedAdministrationOriginTenantIds(result([first], [first])), [first]);
});

for (const [name, output] of [
  ['missing result', 'progress only'], ['duplicate result', result([]) + '\n' + result([])],
  ['foreign shape', 'seed-result: {"version":2,"freshTenantIds":[],"secret":"private-canary"}'],
  ['duplicate JSON field', 'seed-result: {"version":2,"version":2,"freshTenantIds":[]}'],
  ['unknown version', 'seed-result: {"version":3,"freshTenantIds":[],"qualifiedOriginTenantIds":[]}'],
  ['wrong array', 'seed-result: {"version":2,"freshTenantIds":"all"}'],
  ['duplicate ID', result([first, first])], ['noncanonical ID', result(['01930000-0000-7000-8000-00000000000A'])],
  ['too many IDs', result([first, second, first])], ['duplicate qualified ID', result([], [first, first])],
  ['invalid qualified ID', result([], ['private-canary'])],
  ['union too large', result([first, second], ['01930000-0000-7000-8000-000000000003'])], ['raw value', result(['private-canary'])],
]) test(`origin selection refuses ${name} with fixed diagnostics`, () => {
  assert.throws(() => seedAdministrationOriginTenantIds(output), { message: 'seed: administration-origin selection was refused.' });
});

test('unqualified legacy selection opens no child; qualified repeat and explicit all use private stdin and app-only environment', async () => {
  const calls = [];
  const processes = { runPrivateInput: async (...args) => calls.push(args) };
  const values = {
    ConnectionStrings__Default: 'test-app-origin-canary',
    Identity__AuthorityId: first, Authentication__Tenant__Issuer: 'http://localhost:8080/realms/learnstack',
    ConnectionStrings__Migration: 'private-migration-canary', KEYCLOAK_ADMIN_PASSWORD: 'private-provider-canary',
  };
  await adoptSeedAdministrationOrigins(processes, values, []);
  assert.equal(calls.length, 0);
  await adoptSeedAdministrationOrigins(processes, values, seedAdministrationOriginTenantIds(result([], [first])));
  assert.equal(calls.length, 1);
  assert.deepEqual(JSON.parse(calls[0][2]), { version: 1, tenantIds: [first] });
  await adoptSeedAdministrationOrigins(processes, values, 'all');
  assert.equal(calls.length, 2);
  const [command, argv, input, options] = calls[1];
  assert.equal(command, 'dotnet');
  assert.equal(argv.at(-1), '--seed-admin-origins-stdin');
  assert.deepEqual(JSON.parse(input), { version: 1, tenantIds: 'all' });
  assert.equal(options.env.ConnectionStrings__Default, values.ConnectionStrings__Default);
  assert.equal(options.env.ConnectionStrings__Migration, undefined);
  assert.equal(options.env.KEYCLOAK_ADMIN_PASSWORD, undefined);
  assert.ok(!JSON.stringify(argv).includes('canary'));
});

test('an upgrade failure preserves the durable preparation notice without payload diagnostics', async () => {
  const processes = { runPrivateInput: async () => { throw new Error('private-provider-canary'); } };
  const values = { ConnectionStrings__Default: 'test-app', Identity__AuthorityId: first,
    Authentication__Tenant__Issuer: 'http://localhost:8080/realms/learnstack' };
  await assert.rejects(adoptSeedAdministrationOrigins(processes, values, [first], true), error => {
    assert.equal(error.preparationDispatched, true);
    assert.equal(error.stage, 'identity');
    assert.equal(error.reason, 'seed_refused');
    assert.ok(!realmFailureNotice(error).includes('private-provider-canary'));
    return true;
  });
});
