import assert from 'node:assert/strict';
import test from 'node:test';
import { planRealmEvents } from './development-realm-events.mjs';
import { buildRealmPlan, buildRealmPreparation } from './development-realm-plan.mjs';
import { desired, manifest, secret, provider } from './development-realm-test-fixture.mjs';

const plan = fixture => buildRealmPlan({ admin: fixture.admin, desired, manifest, webSecret: secret });
const refused = error => error.stage === 'plan' && error.reason === 'event_policy_conflict';

test('a converged policy has no write and independently verifies listener/admin preservation', async () => {
  const fixture = provider();
  const before = structuredClone(fixture.state.eventsConfig);
  const result = await planRealmEvents(fixture.admin);
  assert.equal(result.changed, false);
  await result.verify();
  assert.deepEqual(fixture.writes, []);
  assert.deepEqual(fixture.state.eventsConfig, before);
  assert.deepEqual(fixture.reads, ['events/config', 'events/config']);
});

test('disabled default storage needs one bounded empty-history read and retains effective prior types', async () => {
  const fixture = provider();
  Object.assign(fixture.state.eventsConfig, { eventsEnabled: false, eventsExpiration: 0,
    enabledEventTypes: ['LOGIN', 'UPDATE_CREDENTIAL', 'CUSTOM_REQUIRED_ACTION'],
    eventsListeners: ['jboss-logging', 'existing-listener'], adminEventsEnabled: true, adminEventsDetailsEnabled: true });
  const result = await plan(fixture);
  assert.deepEqual(fixture.writes, []);
  assert.equal(result.actions.length, 1);
  for (const action of result.actions) await action.apply();
  await result.verify();
  assert.equal(fixture.state.eventsConfig.eventsExpiration, 604800);
  assert.equal(fixture.state.eventsConfig.eventsEnabled, true);
  assert.ok(fixture.state.eventsConfig.enabledEventTypes.includes('REFRESH_TOKEN_ERROR'));
  assert.ok(fixture.state.eventsConfig.enabledEventTypes.includes('UPDATE_CREDENTIAL'));
  assert.deepEqual(fixture.state.eventsConfig.eventsListeners, ['existing-listener', 'jboss-logging']);
  assert.equal(fixture.state.eventsConfig.adminEventsEnabled, true);
  assert.equal(fixture.state.eventsConfig.adminEventsDetailsEnabled, true);
  assert.ok(fixture.reads.includes('events?first=0&max=1'));
  assert.equal((await plan(fixture)).actions.length, 0);
});

for (const enabled of [true, false]) {
  test(`longer finite retention survives convergence with storage initially ${enabled}`, async () => {
    const fixture = provider();
    Object.assign(fixture.state.eventsConfig, { eventsEnabled: enabled, eventsExpiration: 1209600 });
    const change = await plan(fixture);
    for (const action of change.actions) await action.apply();
    await change.verify();
    assert.equal(fixture.state.eventsConfig.eventsExpiration, 1209600);
    assert.ok(!fixture.reads.some(path => path.startsWith('events?')));
  });
}

for (const [name, patch] of [
  ['enabled indefinite retention', { eventsExpiration: 0 }],
  ['enabled shorter retention', { eventsExpiration: 604799 }],
  ['disabled customized shorter retention', { eventsEnabled: false, eventsExpiration: 10 }],
  ['negative retention', { eventsExpiration: -1 }],
  ['fractional retention', { eventsExpiration: 604800.5 }],
  ['missing retention', { eventsExpiration: undefined }],
  ['nonboolean enablement', { eventsEnabled: 'false' }],
  ['unknown effective selection', { enabledEventTypes: [] }],
  ['duplicate types', { enabledEventTypes: ['LOGIN', 'LOGIN'] }],
  ['invalid type', { enabledEventTypes: ['LOGIN', 'arbitrary/private?value'] }],
  ['invalid listeners', { eventsListeners: 'jboss-logging' }],
  ['invalid admin policy', { adminEventsEnabled: 'false' }],
]) {
  test(`${name} refuses before identity metadata preparation or any write`, async () => {
    const fixture = provider();
    Object.assign(fixture.state.eventsConfig, patch);
    fixture.state.profile.attributes = fixture.state.profile.attributes.filter(entry => !['user_id', 'tenant_id'].includes(entry.name));
    await assert.rejects(buildRealmPreparation({ admin: fixture.admin, desired, manifest, webSecret: secret }), refused);
    assert.deepEqual(fixture.writes, []);
    assert.deepEqual(fixture.reads, ['events/config']);
  });
}

for (const history of [[{ type: 'LOGIN', userId: 'synthetic-private-event' }], null]) {
  test(`default retention adoption rejects ${history === null ? 'malformed' : 'nonempty'} history without returning details`, async () => {
    const fixture = provider();
    Object.assign(fixture.state.eventsConfig, { eventsEnabled: false, eventsExpiration: 0 });
    const get = fixture.admin.get;
    fixture.admin.get = path => path.startsWith('events?') ? history : get(path);
    await assert.rejects(plan(fixture), error => refused(error) && !error.stack.includes('synthetic-private-event'));
    assert.deepEqual(fixture.writes, []);
  });
}

for (const [name, mutate] of [
  ['missing refresh events', value => { value.enabledEventTypes = value.enabledEventTypes.filter(type => type !== 'REFRESH_TOKEN'); }],
  ['lost listener', value => { value.eventsListeners = []; }],
  ['changed admin policy', value => { value.adminEventsEnabled = !value.adminEventsEnabled; }],
  ['changed retention', value => { value.eventsExpiration = 1209600; }],
]) {
  test(`independent policy readback rejects ${name}`, async () => {
    const fixture = provider();
    const change = await plan(fixture);
    mutate(fixture.state.eventsConfig);
    await assert.rejects(change.verify(), error => error.stage === 'readback' && error.reason === 'readback_conflict');
    assert.deepEqual(fixture.writes, []);
  });
}

test('effective type/listener ordering does not manufacture mutations', async () => {
  const fixture = provider();
  fixture.state.eventsConfig.enabledEventTypes.reverse();
  const result = await planRealmEvents(fixture.admin);
  assert.equal(result.changed, false);
  fixture.state.eventsConfig.enabledEventTypes.reverse();
  await result.verify();
});

test('the pinned omitted-zero representation qualifies only a disabled empty store', async () => {
  const fixture = provider();
  fixture.state.eventsConfig.eventsEnabled = false;
  delete fixture.state.eventsConfig.eventsExpiration;
  const change = await plan(fixture);
  for (const action of change.actions) await action.apply();
  await change.verify();
  assert.equal(fixture.state.eventsConfig.eventsExpiration, 604800);
  assert.ok(fixture.reads.includes('events?first=0&max=1'));
});
