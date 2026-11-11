import assert from 'node:assert/strict';
import test from 'node:test';
import { planEventReader, eventReaderDefaultRoleDetachment } from './development-event-reader.mjs';
import { providerWithEventReader } from './development-event-reader-test-fixture.mjs';
import { eventReaderEnvironment } from './development-realm-owner.mjs';
const secret = 'owned-event-reader-test-secret';

test('reader setup creates only its separate client and view-events bindings, then becomes read-only', async () => {
  const fixture = providerWithEventReader();
  const plan = await planEventReader(fixture.admin, secret);
  assert.deepEqual(fixture.writes, []);
  await plan.apply(); await plan.verify();
  const writes = fixture.writes.length;
  const repeated = await planEventReader(fixture.admin, secret);
  assert.equal(repeated.changed, false);
  await repeated.apply(); await repeated.verify();
  assert.equal(fixture.writes.length, writes);
  assert.ok(fixture.writes.every(write => write.path === 'clients' || write.path.includes('owned-event-reader')));
});
for (const [name, change] of [
  ['browser flow', value => { value.reader.standardFlowEnabled = true; }],
  ['direct password', value => { value.reader.directAccessGrantsEnabled = true; }],
  ['full scope', value => { value.reader.fullScopeAllowed = true; }],
  ['different secret', value => { value.reader.secret = 'wrong-retained-reader-secret'; }],
  ['extra mapper', value => { value.reader.protocolMappers.push({ name: 'unowned-mapper' }); }],
  ['extra grant', value => { value.user.roles.clientMappings['realm-management'].mappings.push({ ...value.role, id: 'extra', name: 'manage-events' }); }],
  ['realm grant', value => { value.user.roles.realmMappings = [{ name: 'admin' }]; }],
]) test(`pre-existing reader refuses ${name} without any repair write`, async () => {
  const fixture = providerWithEventReader();
  await (await planEventReader(fixture.admin, secret)).apply();
  change(fixture.readerPolicy());
  const before = fixture.writes.length;
  await assert.rejects(planEventReader(fixture.admin, secret));
  assert.equal(fixture.writes.length, before);
});
test('a caller cannot manufacture a default-role or scope detachment', () => {
  assert.throws(() => eventReaderDefaultRoleDetachment({ path: 'users/foreign/role-mappings/realm' }));
});
for (const [name, source, ambient] of [
  ['absent', '', {}], ['short', 'IdentityEvents__ClientSecret=short\n', {}],
  ['duplicate', `IdentityEvents__ClientSecret=${secret}\nIdentityEvents__ClientSecret=${secret}\n`, {}],
  ['alias', `export IdentityEvents__ClientSecret=${secret}\n`, {}],
  ['web reuse', `IdentityEvents__ClientSecret=${secret}\nKEYCLOAK_WEB_CLIENT_SECRET=${secret}\n`, {}],
  ['ambient conflict', `IdentityEvents__ClientSecret=${secret}\n`, { IdentityEvents__ClientSecret: 'another-reader-secret' }],
]) test(`selected private reader source refuses ${name}`, () => assert.throws(() => eventReaderEnvironment(source, ambient)));
test('selected private reader source accepts one independent credential', () => {
  assert.equal(eventReaderEnvironment(`IdentityEvents__ClientSecret=${secret}\n`, {}), secret);
});
