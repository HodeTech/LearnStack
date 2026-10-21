import assert from 'node:assert/strict';
import { once } from 'node:events';
import { readFile } from 'node:fs/promises';
import http from 'node:http';
import test from 'node:test';
import { reconcileRealm } from './development-realm-core.mjs';
import { RealmError, realmFailure } from './development-realm-errors.mjs';
import { desired, manifest, secret, provider, providerTransport } from './development-realm-test-fixture.mjs';

const privateCanary = 'synthetic-private-token-and-password-41ae';

async function fixture(t, { discovery, discoveryBody, authentication, version, desired = {}, manifest = {} } = {}) {
  const paths = [];
  const sockets = new Set();
  let base;
  const server = http.createServer((request, response) => {
    paths.push([request.method, request.url]);
    response.setHeader('Content-Type', 'application/json');
    if (request.url === '/realms/learnstack/.well-known/openid-configuration') {
      response.end(discoveryBody ?? JSON.stringify({ issuer: discovery ?? new URL('realms/learnstack', base).href }));
    } else if (request.url === '/realms/master/protocol/openid-connect/token') {
      request.resume();
      response.statusCode = authentication?.status ?? 200;
      response.end(authentication?.body ?? JSON.stringify({ access_token: privateCanary, refresh_token: privateCanary, expires_in: 60 }));
    } else if (request.url === '/admin/serverinfo') {
      assert.equal(request.headers.authorization, 'Bearer ' + privateCanary);
      response.statusCode = version?.status ?? 200;
      response.end(version?.body ?? JSON.stringify({ systemInfo: { version: 'unsupported-fixture-version' } }));
    } else {
      response.statusCode = 500;
      response.end('{}');
    }
  });
  server.on('connection', socket => {
    sockets.add(socket);
    socket.once('close', () => sockets.delete(socket));
  });
  t.after(async () => {
    const closed = new Promise(resolve => server.close(resolve));
    for (const socket of sockets) socket.destroy();
    await closed;
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  base = new URL(`http://127.0.0.1:${server.address().port}/`);
  return { paths, invoke: () => reconcileRealm({ baseUrl: base.href,
    expectedIssuer: new URL('realms/learnstack', base).href,
    adminUsername: 'synthetic-admin', adminPassword: privateCanary,
    webSecret: privateCanary, manifest, desired,
  }) };
}

function refusal(stage, reason) {
  return error => {
    assert.ok(error instanceof RealmError);
    assert.equal(realmFailure(error), `realm: ${stage}/${reason}`);
    assert.ok(!error.stack.includes(privateCanary));
    return true;
  };
}

const memoryInput = transport => ({ baseUrl: 'http://localhost:8080/',
  expectedIssuer: 'http://localhost:8080/realms/learnstack', adminUsername: 'synthetic-admin',
  adminPassword: privateCanary, webSecret: secret, desired: structuredClone(desired),
  manifest: structuredClone(manifest), transport });

test('the actual core applies one mutation, verifies full state, and makes an explicit next pass read only', async () => {
  const state = provider(); state.state.realm.accessTokenLifespan = 1800;
  const recording = providerTransport(state);
  assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 1 });
  assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 0 });
  assert.equal(state.writes.length, 1);
  assert.equal(recording.requests.filter(request => request.path === '/admin/realms/learnstack/' && !request.method).length, 6);
});

for (const stage of ['apply', 'readback']) {
  test(`an unexpected ${stage} fault preserves its closed stage and a committed mutation resumes from fresh state`, async () => {
    const state = provider(); state.state.realm.accessTokenLifespan = 1800;
    const recording = providerTransport(state);
    let failed = false;
    const transport = async (url, options) => {
      const response = await recording.transport(url, options);
      if (!failed && options.stage === stage) { failed = true; throw new Error(privateCanary); }
      return response;
    };
    await assert.rejects(reconcileRealm(memoryInput(transport)), refusal(stage, 'response'));
    assert.equal(state.state.realm.accessTokenLifespan, 900);
    assert.equal(state.writes.length, 1);
    assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 0 });
    assert.equal(state.writes.length, 1, 'the uncertain completed write is never automatically replayed');
  });
}

test('invalid endpoint syntax is a closed preflight failure before any transport call', async () => {
  let calls = 0;
  const input = memoryInput(async () => { calls++; });
  input.baseUrl = 'http://[';
  await assert.rejects(reconcileRealm(input), refusal('preflight', 'configuration'));
  assert.equal(calls, 0);
});

for (const stage of ['discovery', 'authentication', 'version']) {
  test(`a valid JSON null ${stage} document is rejected at that same stage`, async t => {
    const f = await fixture(t, stage === 'discovery' ? { discoveryBody: 'null' }
      : stage === 'authentication' ? { authentication: { body: 'null' } } : { version: { body: 'null' } });
    await assert.rejects(f.invoke(), refusal(stage, 'response'));
  });
}

test('short-lived master credentials proactively refresh before Admin reads without replaying an exchange', async t => {
  const state = provider();
  const recording = providerTransport(state);
  let now = 0;
  let validUntil = 60000;
  t.mock.method(performance, 'now', () => now);
  const transport = async (url, options) => {
    if (url.pathname.startsWith('/admin/') && now >= validUntil)
      return { status: 401, bytes: Buffer.from('{}') };
    const result = await recording.transport(url, options);
    if (options.form?.grant_type === 'refresh_token') validUntil = now + 60000;
    if (url.pathname === '/admin/serverinfo') now = 61000;
    return result;
  };
  assert.deepEqual(await reconcileRealm(memoryInput(transport)), { mutations: 0 });
  assert.deepEqual(recording.requests.filter(request => request.path.endsWith('/token'))
    .map(request => request.form.grant_type), ['password', 'refresh_token']);
  assert.equal(recording.requests.filter(request => request.path === '/admin/serverinfo').length, 1);
  assert.equal(state.writes.length, 0);
});

test('an Admin 401 is never replayed and refresh failure never reaches subsequent Admin work', async t => {
  t.mock.method(performance, 'now', () => now);
  let now = 0;
  for (const failure of ['admin', 'refresh']) {
    const state = provider(); state.state.realm.accessTokenLifespan = 1800;
    const recording = providerTransport(state);
    let failures = 0;
    now = 0;
    const transport = async (url, options) => {
      if ((failure === 'admin' && url.pathname === '/admin/realms/learnstack/')
        || (failure === 'refresh' && options.form?.grant_type === 'refresh_token')) {
        failures++;
        return { status: 401, bytes: Buffer.from('{}') };
      }
      const result = await recording.transport(url, options);
      if (failure === 'refresh' && url.pathname === '/admin/serverinfo') now = 56000;
      return result;
    };
    await assert.rejects(reconcileRealm(memoryInput(transport)), refusal(failure === 'admin' ? 'plan' : 'authentication', 'status'));
    assert.equal(failures, 1);
    assert.equal(state.writes.length, 0);
    assert.equal(recording.requests.filter(request => request.path.endsWith('/token')).length, 1);
  }
});

test('refresh is bounded to two rotations within the same pass and cannot form a token-request loop', async t => {
  const state = provider();
  const recording = providerTransport(state);
  let now = 0;
  t.mock.method(performance, 'now', () => now);
  const transport = async (url, options) => {
    const result = await recording.transport(url, options);
    if (url.pathname.startsWith('/admin/')) now += 56000;
    return result;
  };
  await assert.rejects(reconcileRealm(memoryInput(transport)), refusal('authentication', 'deadline'));
  assert.equal(recording.requests.filter(request => request.path.endsWith('/token')).length, 3);
  assert.equal(state.writes.length, 0);
});

test('the 120-second whole-pass deadline closes work across individually successful exchanges and a new pass resumes', async t => {
  const state = provider(); state.state.realm.accessTokenLifespan = 1800;
  const recording = providerTransport(state);
  const deadline = new AbortController();
  const allocations = [];
  t.mock.method(AbortSignal, 'timeout', milliseconds => { allocations.push(milliseconds); return deadline.signal; });
  let observed = false;
  const transport = async (url, options) => {
    const result = await recording.transport(url, options);
    if (options.stage === 'apply') { observed = true; deadline.abort(); }
    return result;
  };
  await assert.rejects(reconcileRealm(memoryInput(transport)), refusal('apply', 'deadline'));
  assert.deepEqual(allocations, [120000], 'a larger whole-pass deadline must fail this proof');
  assert.equal(observed, true);
  assert.equal(state.writes.length, 1);
  assert.equal(recording.requests.some(request => request.stage === 'readback'), false);
  t.mock.restoreAll();
  assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 0 });
  assert.equal(state.writes.length, 1);
});

test('a 999-user census still refuses at the whole-pass deadline under cumulative provider latency', async t => {
  const state = provider();
  while (state.state.users.length < 999) {
    const index = state.state.users.length;
    state.state.users.push({ id: `retained-${index}`, username: `retained-${index}`, attributes: {} });
  }
  const recording = providerTransport(state);
  const deadline = new AbortController();
  let now = 0;
  let budget;
  t.mock.method(performance, 'now', () => now);
  t.mock.method(AbortSignal, 'timeout', milliseconds => { budget = milliseconds; return deadline.signal; });
  const transport = async (url, options) => {
    const result = await recording.transport(url, options);
    // Each successful 40ms exchange is independently below the 5s HTTP cap.
    now += 40;
    if (now >= budget) deadline.abort();
    return result;
  };
  await assert.rejects(reconcileRealm(memoryInput(transport)), refusal('readback', 'deadline'));
  assert.equal(budget, 120000);
  assert.equal(now, 120000);
  assert.equal(recording.requests.length, 3000);
  assert.ok(state.reads.filter(path => path.includes('/groups?')).length > 999);
  assert.deepEqual(state.writes, []);
});

for (const reason of ['cancelled', 'deadline']) {
  for (const outcome of ['reject', 'resolve']) {
    test(`${reason} remains correctly classified when the transport ${outcome}s after abort`, async t => {
      const parent = new AbortController();
      const deadline = new AbortController();
      t.mock.method(AbortSignal, 'timeout', () => deadline.signal);
      let calls = 0;
      await assert.rejects(reconcileRealm({
        baseUrl: 'http://localhost:8080/', expectedIssuer: 'http://localhost:8080/realms/learnstack',
        adminUsername: 'synthetic-admin', adminPassword: privateCanary, webSecret: privateCanary,
        desired: {}, manifest: {}, signal: parent.signal,
        transport: async (_url, { signal, stage }) => {
          calls++;
          if (reason === 'cancelled') parent.abort();
          deadline.abort();
          assert.equal(signal.aborted, true);
          if (outcome === 'reject') throw new RealmError(stage, 'cancelled');
          return { status: 200, bytes: Buffer.from('{}') };
        },
      }), refusal('discovery', reason));
      assert.equal(calls, 1);
    });
  }
}

test('a transport refusal without an active abort is propagated unchanged', async () => {
  const failure = new RealmError('discovery', 'transport');
  await assert.rejects(reconcileRealm({
    baseUrl: 'http://localhost:8080/', expectedIssuer: 'http://localhost:8080/realms/learnstack',
    adminUsername: 'synthetic-admin', adminPassword: privateCanary, webSecret: privateCanary,
    desired: {}, manifest: {}, transport: async () => { throw failure; },
  }), error => error === failure);
});

test('discovery disagreement refuses before exposing administrator credentials', async t => {
  const f = await fixture(t, { discovery: 'https://foreign.fixture.invalid/realms/learnstack' });
  await assert.rejects(f.invoke(), refusal('discovery', 'configuration'));
  assert.deepEqual(f.paths, [['GET', '/realms/learnstack/.well-known/openid-configuration']]);
});

for (const [name, authentication, reason] of [
  ['rejected credentials', { status: 401, body: privateCanary }, 'status'],
  ['malformed credential response', { body: '{' + privateCanary }, 'response'],
  ['missing access token', { body: '{}' }, 'response'],
  ['nonstring access token', { body: '{"access_token":123}' }, 'response'],
]) {
  test(`${name} fails once without version, planning or mutation`, async t => {
    const f = await fixture(t, { authentication });
    await assert.rejects(f.invoke(), refusal('authentication', reason));
    assert.deepEqual(f.paths.map(value => value[0]), ['GET', 'POST']);
    assert.equal(f.paths.length, 2);
  });
}

for (const [name, version, reason] of [
  ['wrong server version', undefined, 'unsupported'],
  ['missing version document', { body: '{}' }, 'unsupported'],
  ['malformed full version document', { body: '{"systemInfo":{"version":"26.6.2"},' }, 'response'],
  ['rejected version read', { status: 403, body: privateCanary }, 'unsupported'],
  ['version response above accepted cap', { body: ' '.repeat(512 * 1024 + 1) }, 'body_limit'],
  ['large malformed version document', { body: '{"systemInfo":{"version":"26.6.2"},' + ' '.repeat(300 * 1024) }, 'response'],
  ['large wrong version document', { body: JSON.stringify({ systemInfo: { version: 'wrong' }, padding: ' '.repeat(300 * 1024) }) }, 'unsupported'],
]) {
  test(`${name} refuses before any realm-state read or mutation`, async t => {
    const f = await fixture(t, { version });
    await assert.rejects(f.invoke(), refusal('version', reason));
    assert.deepEqual(f.paths, [
      ['GET', '/realms/learnstack/.well-known/openid-configuration'],
      ['POST', '/realms/master/protocol/openid-connect/token'],
      ['GET', '/admin/serverinfo'],
    ]);
  });
}

test('an exact 512 KiB version document reaches planning only after the complete valid read', async t => {
  const base = JSON.stringify({ systemInfo: { version: '26.6.2' }, padding: '' });
  const body = JSON.stringify({ systemInfo: { version: '26.6.2' }, padding: ' '.repeat(512 * 1024 - Buffer.byteLength(base)) });
  assert.equal(Buffer.byteLength(body), 512 * 1024);
  const desired = JSON.parse(await readFile(new URL('../infra/keycloak/realms/learnstack.json', import.meta.url), 'utf8'));
  const manifest = { users: desired.users.map(user => ({ username: user.username, email: user.email,
    firstName: user.firstName, lastName: user.lastName,
    realmRole: user.realmRoles[0],
    userId: user.attributes.user_id[0], tenantId: user.attributes.tenant_id[0],
    organizationId: user.attributes.organization_id?.[0] ?? null,
    accountCapability: { clientId: 'account', role: 'manage-account' } })) };
  const f = await fixture(t, { version: { body }, desired, manifest });
  // This deliberately has no realm state: reaching the first read witnesses
  // accepted version admission without inventing a successful reconciliation.
  await assert.rejects(f.invoke(), refusal('plan', 'status'));
  assert.equal(f.paths.length, 4);
  assert.deepEqual(f.paths[2], ['GET', '/admin/serverinfo']);
  assert.deepEqual(f.paths[3], ['GET', '/admin/realms/learnstack/']);
});
