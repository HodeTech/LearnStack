import assert from 'node:assert/strict';
import test from 'node:test';
import { planRealmEvents } from './development-realm-events.mjs';
import { buildRealmPlan, buildRealmPreparation } from './development-realm-plan.mjs';
import { desired, manifest, secret, provider } from './development-realm-test-fixture.mjs';

const plan = fixture => buildRealmPlan({ admin: fixture.admin, desired, manifest, webSecret: secret });
const refused = error => error.stage === 'plan' && error.reason === 'event_policy_conflict';
const requiredTypes = [
  'LOGIN', 'LOGIN_ERROR', 'LOGOUT', 'LOGOUT_ERROR', 'CODE_TO_TOKEN',
  'CODE_TO_TOKEN_ERROR', 'REFRESH_TOKEN', 'REFRESH_TOKEN_ERROR',
];

for (const [name, mutate] of [
  ['disabled storage', value => { value.eventsEnabled = false; }],
  ['missing storage flag', value => { delete value.eventsEnabled; }],
  ['wrong retention', value => { value.eventsExpiration = 604801; }],
  ['missing retention', value => { delete value.eventsExpiration; }],
  ['nonarray selection', value => { value.enabledEventTypes = null; }],
  ...requiredTypes.map(type => [`missing ${type}`, value => {
    value.enabledEventTypes = value.enabledEventTypes.filter(current => current !== type);
  }]),
]) {
  test(`the desired export rejects ${name} before ordinary or preparation reads`, async () => {
    for (const build of [buildRealmPlan, buildRealmPreparation]) {
      const fixture = provider();
      const declaration = structuredClone(desired);
      mutate(declaration);
      await assert.rejects(build({ admin: fixture.admin, desired: declaration, manifest, webSecret: secret }),
        error => error.stage === 'plan' && error.reason === 'unsupported');
      assert.deepEqual(fixture.reads, []);
      assert.deepEqual(fixture.writes, []);
    }
  });
}

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
    enabledEventTypes: ['UPDATE_CREDENTIAL', 'CUSTOM_REQUIRED_ACTION'],
    eventsListeners: ['jboss-logging', 'existing-listener'], adminEventsEnabled: true, adminEventsDetailsEnabled: true });
  const result = await plan(fixture);
  assert.deepEqual(fixture.writes, []);
  assert.equal(result.actions.length, 1);
  for (const action of result.actions) await action.apply();
  await result.verify();
  assert.equal(fixture.state.eventsConfig.eventsExpiration, 604800);
  assert.equal(fixture.state.eventsConfig.eventsEnabled, true);
  assert.deepEqual(fixture.state.eventsConfig.enabledEventTypes, [
    'LOGIN', 'LOGIN_ERROR', 'LOGOUT', 'LOGOUT_ERROR', 'CODE_TO_TOKEN',
    'CODE_TO_TOKEN_ERROR', 'REFRESH_TOKEN', 'REFRESH_TOKEN_ERROR',
    'UPDATE_CREDENTIAL', 'CUSTOM_REQUIRED_ACTION',
  ].sort());
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
  ['each missing required event', value => value],
  ['lost listener', value => { value.eventsListeners = []; }],
  ['changed admin policy', value => { value.adminEventsEnabled = !value.adminEventsEnabled; }],
  ['changed retention', value => { value.eventsExpiration = 1209600; }],
  ['disabled storage', value => { value.eventsEnabled = false; }],
]) {
  test(`independent policy readback rejects ${name}`, async () => {
    const fixture = provider();
    const change = await plan(fixture);
    const before = structuredClone(fixture.state.eventsConfig);
    for (const removed of name === 'each missing required event' ? [
      'LOGIN', 'LOGIN_ERROR', 'LOGOUT', 'LOGOUT_ERROR', 'CODE_TO_TOKEN',
      'CODE_TO_TOKEN_ERROR', 'REFRESH_TOKEN', 'REFRESH_TOKEN_ERROR',
    ] : [undefined]) {
      fixture.state.eventsConfig = structuredClone(before);
      if (removed) fixture.state.eventsConfig.enabledEventTypes = before.enabledEventTypes.filter(type => type !== removed);
      else mutate(fixture.state.eventsConfig);
      await assert.rejects(change.verify(), error => error.stage === 'readback' && error.reason === 'readback_conflict');
    }
    assert.deepEqual(fixture.writes, []);
  });
}

for (const count of [256, 257]) {
  test(`effective selection has an independent ${count}-type input boundary`, async () => {
    const fixture = provider();
    fixture.state.eventsConfig.enabledEventTypes = [
      ...requiredTypes,
      ...Array.from({ length: count - requiredTypes.length }, (_, index) => `CUSTOM_${index}`),
    ];
    if (count === 256) {
      const change = await planRealmEvents(fixture.admin);
      assert.equal(change.changed, false);
      assert.equal(change.body.enabledEventTypes.length, 256);
      await change.verify();
    } else await assert.rejects(planRealmEvents(fixture.admin), refused);
    assert.deepEqual(fixture.writes, []);
  });
}

for (const count of [255, 256]) {
  test(`adding the missing required type respects the ${count}-type post-union boundary`, async () => {
    const fixture = provider();
    fixture.state.eventsConfig.enabledEventTypes = [
      ...requiredTypes.slice(1),
      ...Array.from({ length: count - requiredTypes.length + 1 }, (_, index) => `CUSTOM_${index}`),
    ];
    if (count === 255) {
      const change = await planRealmEvents(fixture.admin);
      assert.equal(change.body.enabledEventTypes.length, 256);
      assert.ok(change.body.enabledEventTypes.includes('LOGIN'));
    } else await assert.rejects(planRealmEvents(fixture.admin),
      error => error.stage === 'plan' && error.reason === 'body_limit');
    assert.deepEqual(fixture.writes, []);
  });
}

for (const overflow of [false, true]) {
  test(`the normalized event document has its own exact decoded-byte boundary: overflow=${overflow}`, async () => {
    const fixture = provider();
    fixture.state.eventsConfig.padding = '';
    const bytes = value => Buffer.byteLength(JSON.stringify(value));
    fixture.state.eventsConfig.padding = 'x'.repeat(256 * 1024 - bytes(fixture.state.eventsConfig) + Number(overflow));
    if (overflow) await assert.rejects(planRealmEvents(fixture.admin),
      error => error.stage === 'plan' && error.reason === 'body_limit');
    else {
      const change = await planRealmEvents(fixture.admin);
      assert.equal(bytes(change.body), 256 * 1024);
      await change.verify();
    }
    assert.deepEqual(fixture.writes, []);
  });
}

test('event readback independently rejects decoded-byte overflow before comparing state', async () => {
  const fixture = provider();
  fixture.state.eventsConfig.padding = '';
  fixture.state.eventsConfig.padding = 'x'.repeat(256 * 1024 - Buffer.byteLength(JSON.stringify(fixture.state.eventsConfig)));
  const change = await planRealmEvents(fixture.admin);
  await change.verify();
  fixture.state.eventsConfig.padding += 'x';
  await assert.rejects(change.verify(), error => error.stage === 'readback' && error.reason === 'body_limit');
  assert.deepEqual(fixture.writes, []);
});

for (const overflow of [false, true]) {
  test(`event expansion independently enforces its own exact decoded-byte boundary: overflow=${overflow}`, async () => {
    const fixture = provider();
    Object.assign(fixture.state.eventsConfig, { enabledEventTypes: ['LOGIN'], padding: '' });
    const projected = { ...fixture.state.eventsConfig, enabledEventTypes: [...requiredTypes].sort() };
    const bytes = value => Buffer.byteLength(JSON.stringify(value));
    fixture.state.eventsConfig.padding = 'x'.repeat(256 * 1024 - bytes(projected) + Number(overflow));
    assert.ok(bytes(fixture.state.eventsConfig) < 256 * 1024);
    if (overflow) await assert.rejects(planRealmEvents(fixture.admin),
      error => error.stage === 'plan' && error.reason === 'body_limit');
    else {
      const change = await planRealmEvents(fixture.admin);
      assert.equal(bytes(change.body), 256 * 1024);
      assert.deepEqual(change.body.enabledEventTypes, [...requiredTypes].sort());
    }
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

for (const preparing of [false, true]) {
  for (const overflow of [false, true]) {
    test(`${preparing ? 'preparation' : 'ordinary planning'} budgets event expansion at the exact response boundary: overflow=${overflow}`, async () => {
      const fixture = provider();
      Object.assign(fixture.state.eventsConfig, { eventsEnabled: false, eventsExpiration: 0, enabledEventTypes: ['LOGIN'] });
      Object.assign(fixture.state.realm, { eventsEnabled: false, eventsExpiration: 0, enabledEventTypes: ['LOGIN'], padding: '' });
      if (preparing) fixture.state.profile.attributes = fixture.state.profile.attributes
        .filter(entry => !['user_id', 'tenant_id', 'organization_id'].includes(entry.name));
      const events = await planRealmEvents(fixture.admin);
      const projected = { ...fixture.state.realm, accessTokenLifespan: 900,
        eventsEnabled: events.body.eventsEnabled, eventsExpiration: events.body.eventsExpiration,
        enabledEventTypes: events.body.enabledEventTypes };
      const bytes = value => Buffer.byteLength(JSON.stringify(value));
      fixture.state.realm.padding = 'x'.repeat(256 * 1024 - bytes(projected) + Number(overflow));
      assert.ok(bytes(fixture.state.realm) <= 256 * 1024);
      const attempt = () => preparing
        ? buildRealmPreparation({ admin: fixture.admin, desired, manifest, webSecret: secret }) : plan(fixture);
      if (overflow) {
        await assert.rejects(attempt(), error => error.stage === 'plan' && error.reason === 'body_limit');
      } else {
        const result = await attempt();
        assert.ok(result);
        if (!preparing) {
          assert.equal(result.actions.length, 1);
          for (const action of result.actions) await action.apply();
          await result.verify();
          assert.equal(bytes(fixture.state.realm), 256 * 1024);
        }
      }
      if (preparing || overflow) assert.deepEqual(fixture.writes, []);
    });
  }
}
