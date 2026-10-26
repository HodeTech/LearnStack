import assert from 'node:assert/strict';
import { once } from 'node:events';
import { readFile } from 'node:fs/promises';
import http from 'node:http';
import test from 'node:test';
import { reconcileRealm } from './development-realm-core.mjs';
import { RealmError, realmFailure, realmFailureNotice } from './development-realm-errors.mjs';
import { desired, manifest, secret, provider, stockProvider, providerTransport } from './development-realm-test-fixture.mjs';

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

test('the private handoff receives exact Admin subjects only after final authenticated readback', async () => {
  const state = provider();
  const recording = providerTransport(state);
  let handed;
  const result = await reconcileRealm({ ...memoryInput(recording.transport), afterVerified: witness => {
    assert.ok(recording.requests.filter(request => request.path === '/admin/realms/learnstack/' && !request.method).length >= 3);
    assert.equal(witness.issuer, 'http://localhost:8080/realms/learnstack');
    assert.equal(witness.version, 1);
    assert.equal(witness.users.length, 4);
    assert.deepEqual(witness.users.map(user => user.actorId), manifest.users.map(user => user.userId));
    for (let index = 0; index < 4; index++) {
      const actual = state.state.users.find(user => user.username === manifest.users[index].username);
      assert.equal(witness.users[index].opaqueSub, actual.id);
    }
    assert.equal(Object.isFrozen(witness), true);
    assert.equal(Object.isFrozen(witness.users), true);
    handed = witness;
  } });
  assert.ok(handed);
  assert.deepEqual(result, { mutations: 0 });
  assert.ok(!JSON.stringify(result).includes('opaqueSub'));
});

test('an unsuccessful final readback never dispatches the private identity consumer', async () => {
  const state = provider();
  const recording = providerTransport(state);
  let reads = 0, dispatched = false;
  const transport = async (url, options) => {
    const result = await recording.transport(url, options);
    if (url.pathname === '/admin/realms/learnstack/' && !options.method && ++reads === 3)
      return { ...result, bytes: Buffer.from(JSON.stringify({ ...JSON.parse(result.bytes), accessTokenLifespan: 1800 })) };
    return result;
  };
  await assert.rejects(reconcileRealm({ ...memoryInput(transport), afterVerified: () => { dispatched = true; } }),
    refusal('readback', 'readback_conflict'));
  assert.equal(dispatched, false);
});

test('a private consumer failure is closed and never exposes provider facts in public results', async () => {
  const state = provider();
  const recording = providerTransport(state);
  await assert.rejects(reconcileRealm({ ...memoryInput(recording.transport), afterVerified: witness => {
    throw new Error(witness.users[0].opaqueSub + privateCanary);
  } }), refusal('identity', 'seed_refused'));
});

test('the actual core applies one mutation, verifies full state, and makes an explicit next pass read only', async () => {
  const state = provider(); state.state.realm.accessTokenLifespan = 1800;
  const recording = providerTransport(state);
  assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 1 });
  assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 0 });
  assert.equal(state.writes.length, 1);
  assert.equal(recording.requests.filter(request => request.path === '/admin/realms/learnstack/' && !request.method).length, 6);
});

test('a successful independent readback still requires an empty final fresh plan', async () => {
  const state = provider();
  const recording = providerTransport(state);
  let realmReads = 0;
  const transport = async (url, options) => {
    const response = await recording.transport(url, options);
    if (url.pathname === '/admin/realms/learnstack/' && !options.method && ++realmReads === 3) {
      assert.equal(options.stage, 'readback');
      const value = JSON.parse(response.bytes.toString('utf8'));
      value.accessTokenLifespan = 1800;
      return { ...response, bytes: Buffer.from(JSON.stringify(value)) };
    }
    return response;
  };
  await assert.rejects(reconcileRealm(memoryInput(transport)), refusal('readback', 'readback_conflict'));
  assert.equal(realmReads, 3, 'planning and its independent verification completed before the final fresh-plan control');
  assert.deepEqual(state.writes, [], 'the remaining change is refused, never automatically applied');
  assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 0 });
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

test('valid but forbidden administrator destinations refuse before even a discovery transport call', async () => {
  for (const baseUrl of [
    'http://provider.fixture.invalid/', 'http://127.0.0.2/', 'http://localhost.fixture.invalid/',
    'http://[::ffff:127.0.0.1]/', 'ftp://127.0.0.1/', 'http://127.0.0.1/proxy/',
    'http://127.0.0.1/?target=foreign', 'http://127.0.0.1/#fragment',
    'http://synthetic-user@127.0.0.1/', 'http://:synthetic-password@127.0.0.1/',
  ]) {
    let calls = 0;
    const input = memoryInput(async () => { calls++; throw new Error('forbidden destination reached'); });
    input.baseUrl = baseUrl;
    input.expectedIssuer = new URL('realms/learnstack', baseUrl).href;
    await assert.rejects(reconcileRealm(input), refusal('preflight', 'configuration'));
    assert.equal(calls, 0);
  }
});

test('issuer identity is exact before credential use while every supported loopback host remains usable', async () => {
  for (const expectedIssuer of [
    'http://localhost:8080/realms/learnstack/', 'http://127.0.0.1:8080/realms/learnstack',
    'http://localhost:8081/realms/learnstack', 'https://localhost:8080/realms/learnstack',
    'http://localhost:8080/realms/learnstack-hub', 'http://localhost:8080/realms/learnstack?extra=true',
  ]) {
    let calls = 0;
    const input = memoryInput(async () => { calls++; });
    input.expectedIssuer = expectedIssuer;
    await assert.rejects(reconcileRealm(input), refusal('preflight', 'configuration'));
    assert.equal(calls, 0);
  }
  for (const host of ['localhost', '127.0.0.1', '[::1]']) {
    const recording = providerTransport(provider());
    const input = memoryInput(recording.transport);
    input.baseUrl = `http://${host}:8080/`;
    input.expectedIssuer = new URL('realms/learnstack', input.baseUrl).href;
    assert.deepEqual(await reconcileRealm(input), { mutations: 0 });
    assert.ok(recording.requests.some(request => request.path === '/realms/master/protocol/openid-connect/token'));
  }
});

test('password and bearer custody use only the selected fixed master and tenant Admin destinations', async () => {
  const state = provider(); state.state.realm.accessTokenLifespan = 1800;
  const recording = providerTransport(state);
  const observed = [];
  const input = memoryInput(async (url, options) => {
    observed.push({ url: new URL(url), options: structuredClone({ ...options, signal: undefined }) });
    return recording.transport(url, options);
  });
  assert.deepEqual(await reconcileRealm(input), { mutations: 1 });
  const passwords = observed.filter(({ options }) => options.form?.grant_type === 'password');
  assert.equal(passwords.length, 1);
  for (const { url, options } of observed) {
    assert.equal(url.origin, new URL(input.baseUrl).origin);
    assert.equal(url.username + url.password + url.hash, '');
    assert.ok(!url.href.includes(privateCanary));
    if (options.form) {
      assert.equal(url.pathname, '/realms/master/protocol/openid-connect/token');
      assert.equal(url.search, '');
      assert.equal(options.method, 'POST');
      assert.deepEqual(options.form, { client_id: 'admin-cli', grant_type: 'password',
        username: input.adminUsername, password: input.adminPassword });
      assert.equal(options.bearer, undefined);
    } else if (options.bearer) {
      assert.ok(url.pathname === '/admin/serverinfo' || url.pathname.startsWith('/admin/realms/learnstack/'));
      assert.ok(!JSON.stringify(options.json ?? {}).includes(privateCanary));
    } else {
      assert.equal(url.pathname, '/realms/learnstack/.well-known/openid-configuration');
      assert.equal(options.json, undefined);
    }
  }
});

test('Admin read status and malformed successful JSON refuse before a pending mutation', async () => {
  for (const [status, bytes, reason] of [
    [204, '{}', 'status'], [403, '{}', 'status'], [500, '{}', 'status'],
    [200, '{', 'response'], [200, '', 'response'], [200, 'null', 'response'], [200, '[]', 'response'],
  ]) {
    const state = provider(); state.state.realm.accessTokenLifespan = 1800;
    const recording = providerTransport(state);
    let refusals = 0;
    const transport = async (url, options) => {
      if (url.pathname === '/admin/realms/learnstack/') {
        refusals++;
        return { status, bytes: Buffer.from(bytes) };
      }
      return recording.transport(url, options);
    };
    await assert.rejects(reconcileRealm(memoryInput(transport)), refusal('plan', reason));
    assert.equal(refusals, 1);
    assert.deepEqual(state.writes, []);
    assert.equal(state.state.realm.accessTokenLifespan, 1800);
  }
});

test('failed mutation statuses and malformed import receipts remain failures even after the provider committed', async () => {
  for (const [kind, status, bytes, reason] of [
    ['put', 403, '{}', 'status'], ['put', 409, '{}', 'status'], ['put', 500, '{}', 'status'],
    ['post', 500, '{}', 'status'], ['post', 201, '{}', 'status'],
    ['post', 200, '{', 'response'], ['post', 200, '[]', 'response'],
  ]) {
    const state = provider();
    if (kind === 'put') state.state.realm.accessTokenLifespan = 1800;
    else state.state.users.pop();
    const recording = providerTransport(state);
    let injected = 0;
    const transport = async (url, options) => {
      const result = await recording.transport(url, options);
      if (options.stage === 'apply' && options.method === (kind === 'put' ? 'PUT' : 'POST')) {
        injected++;
        return { status, bytes: Buffer.from(bytes) };
      }
      return result;
    };
    await assert.rejects(reconcileRealm(memoryInput(transport)), refusal('apply', reason));
    assert.equal(injected, 1);
    assert.equal(state.writes.length, 1, 'a failed mutation response is never replayed');
    assert.equal(recording.requests.some(request => request.stage === 'readback'), false);
    assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 0 });
    assert.equal(state.writes.length, 1, 'a later explicit run qualifies durable state instead of repeating the write');
  }
});

test('successful status cannot replace independent readback when the provider silently loses a mutation', async () => {
  const state = provider(); state.state.realm.accessTokenLifespan = 1800;
  const recording = providerTransport(state);
  let writes = 0;
  const transport = async (url, options) => {
    if (options.method === 'PUT') { writes++; return { status: 204, bytes: Buffer.alloc(0) }; }
    return recording.transport(url, options);
  };
  await assert.rejects(reconcileRealm(memoryInput(transport)), refusal('readback', 'readback_conflict'));
  assert.equal(writes, 1);
  assert.equal(state.state.realm.accessTokenLifespan, 1800);
  assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 1 });
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
  assert.deepEqual(f.paths[3], ['GET', '/admin/realms/learnstack/events/config']);
});


test('the actual core removes only three qualified console assignments and converges without a marker', async () => {
  const fixture = await stockProvider();
  const before = structuredClone(fixture.state);
  const recording = providerTransport(fixture);
  assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 4 });
  const removed = fixture.writes.filter(write => write.method === 'DELETE');
  assert.deepEqual(removed.map(write => write.path).sort(), [
    `${fixture.stock.base}/default-client-scopes/${fixture.stock.capture[fixture.stock.scopePath('roles')].id}`,
    ...['microprofile-jwt', 'offline_access'].map(name =>
      `${fixture.stock.base}/optional-client-scopes/${fixture.stock.capture[fixture.stock.scopePath(name)].id}`),
  ].sort());
  assert.equal(fixture.writes.filter(write => write.method === 'POST').length, 1);
  assert.ok(fixture.writes.filter(write => write.method === 'POST')
    .every(write => write.path === `${fixture.stock.base}/protocol-mappers/models`));
  const console = fixture.state.clients.find(client => client.clientId === 'account-console');
  const expectedConsole = before.clients.find(client => client.clientId === 'account-console');
  expectedConsole.defaultClientScopes = expectedConsole.defaultClientScopes.filter(name => name !== 'roles');
  expectedConsole.optionalClientScopes = expectedConsole.optionalClientScopes.filter(name => !['microprofile-jwt', 'offline_access'].includes(name));
  expectedConsole.protocolMappers.push(structuredClone(console.protocolMappers.at(-1)));
  assert.deepEqual(fixture.state, before, 'global definitions, other assignments, users and grants are preserved');
  assert.equal(console.defaultClientScopes.length, 5);
  assert.equal(console.optionalClientScopes.length, 3);
  assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 0 });
  assert.equal(fixture.writes.length, 4);
});

test('partial assignment deletion commits are never replayed and explicit reruns requalify the remaining edges', async () => {
  for (const outcome of [200, 201, 202, 409, 500, 'lost_ack', 'cancelled']) {
    for (const ordinal of [1, 2, 3]) {
      const fixture = await stockProvider();
      const recording = providerTransport(fixture);
      const stop = new AbortController();
      let seen = 0;
      let failedPath;
      const transport = async (url, options) => {
        const response = await recording.transport(url, options);
        if (options.method === 'DELETE' && ++seen === ordinal) {
          failedPath = url.pathname;
          if (outcome === 'lost_ack') throw new Error(privateCanary);
          if (outcome === 'cancelled') { stop.abort(); return response; }
          return { status: outcome, bytes: Buffer.alloc(0) };
        }
        return response;
      };
      const input = { ...memoryInput(transport), signal: stop.signal };
      const reason = outcome === 'lost_ack' ? 'response' : outcome === 'cancelled' ? 'cancelled' : 'status';
      await assert.rejects(reconcileRealm(input), refusal('apply', reason));
      assert.equal(fixture.writes.length, ordinal, 'the owner stops at the exact uncertain durable prefix');
      assert.equal(recording.requests.some(request => request.stage === 'readback'), false);
      const firstFreshRead = fixture.reads.length;
      assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 4 - ordinal });
      assert.equal(recording.requests.filter(request => request.method === 'DELETE'
        && request.path === failedPath).length, 1, 'the durable deletion is never replayed');
      assert.equal(recording.requests.filter(request => request.method === 'DELETE').length, 3);
      assert.equal(new Set(recording.requests.filter(request => request.method === 'DELETE').map(request => request.path)).size, 3);
      assert.equal(fixture.reads.slice(firstFreshRead).some(path => path.startsWith('roles-by-id/')
        && !path.endsWith('/composites')), ordinal < 3,
      'only a remaining incompatible edge enters stock qualification; the final edge hands off to compatible mode');
      const console = fixture.state.clients.find(client => client.clientId === 'account-console');
      assert.equal(console.defaultClientScopes.length, 5);
      assert.equal(console.optionalClientScopes.length, 3);
      assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 0 });
      assert.equal(fixture.writes.length, 4);
    }
  }
});

test('every incompatible subset hands its last deletion to compatible preservation on the next pass', async () => {
  for (let mask = 1; mask < 8; mask++) {
    const fixture = await stockProvider();
    const console = fixture.state.clients.find(client => client.clientId === 'account-console');
    if (!(mask & 1)) console.defaultClientScopes = console.defaultClientScopes.filter(name => name !== 'roles');
    console.optionalClientScopes = console.optionalClientScopes.filter(name =>
      (name !== 'microprofile-jwt' || mask & 2) && (name !== 'offline_access' || mask & 4));
    const remaining = [1, 2, 4].filter(bit => mask & bit).length;
    const recording = providerTransport(fixture);
    assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: remaining + 1 });
    assert.equal(fixture.writes.filter(write => write.method === 'DELETE').length, remaining);
    assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 0 });
    assert.equal(console.defaultClientScopes.length, 5);
    assert.equal(console.optionalClientScopes.length, 3);
  }
});

test('a compatible customized graph wins before the stock-name exception and keeps every assignment', async () => {
  const fixture = await stockProvider();
  const console = fixture.state.clients.find(client => client.clientId === 'account-console');
  console.optionalClientScopes = console.optionalClientScopes.filter(name => !['microprofile-jwt', 'offline_access'].includes(name));
  const roles = fixture.state.scopes.find(scope => scope.name === 'roles');
  roles.description = 'retained compatible customization';
  roles.protocolMappers = roles.protocolMappers.filter(mapper => mapper.protocolMapper === 'oidc-audience-resolve-mapper');
  console.protocolMappers = [];
  const before = structuredClone(roles);
  const defaults = structuredClone(console.defaultClientScopes);
  const recording = providerTransport(fixture);
  assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 1 });
  assert.equal(fixture.writes.some(write => write.method === 'DELETE'), false);
  assert.deepEqual(roles, before);
  assert.deepEqual(console.defaultClientScopes, defaults);
  assert.equal(fixture.reads.some(path => path.startsWith('roles-by-id/') && !path.endsWith('/composites')), false,
    'the extra stock qualifier does not run for the compatible customized graph');
  assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 0 });
});

test('stock graph transport or response failures never authorize any pending mutation', async () => {
  for (const kind of ['transport', 'status', 'json']) {
    const fixture = await stockProvider();
    fixture.state.realm.accessTokenLifespan = 1800;
    const recording = providerTransport(fixture);
    let reached = false;
    const transport = async (url, options) => {
      if (/\/roles-by-id\/[^/]+$/.test(url.pathname)) {
        reached = true;
        if (kind === 'transport') throw new Error(privateCanary);
        return { status: kind === 'status' ? 503 : 200, bytes: Buffer.from(kind === 'json' ? '{' : '{}') };
      }
      return recording.transport(url, options);
    };
    await assert.rejects(reconcileRealm(memoryInput(transport)), refusal('plan', kind === 'status' ? 'status' : 'response'));
    assert.equal(reached, true);
    assert.deepEqual(fixture.writes, []);
  }
});

test('stock normalization refuses a pending ordinary repair of its unchanged global baseline', async () => {
  const fixture = await stockProvider();
  const recording = providerTransport(fixture);
  const input = memoryInput(recording.transport);
  const basic = input.desired.clientScopes.find(scope => scope.name === 'basic');
  basic.protocolMappers.find(mapper => mapper.protocolMapper === 'oidc-sub-mapper').config['introspection.token.claim'] = 'false';
  await assert.rejects(reconcileRealm(input), refusal('plan', 'unsupported'));
  assert.ok(fixture.reads.some(path => path.startsWith('roles-by-id/') && !path.endsWith('/composites')),
    'the independently qualified stock path is reached before rejecting the conflicting projected delta');
  assert.deepEqual(fixture.writes, []);
});

function preparationProvider() {
  const fixture = provider();
  fixture.state.profile.attributes = fixture.state.profile.attributes
    .filter(attribute => !['user_id', 'tenant_id', 'organization_id'].includes(attribute.name));
  delete fixture.state.profile.unmanagedAttributePolicy;
  return fixture;
}

const preparationNotice = 'realm: profile metadata may already be durable; inspect current realm state before an explicit rerun; see infra/keycloak/README.md';

function preparationFailure(stage, reason) {
  return error => {
    refusal(stage, reason)(error);
    assert.equal(error.preparationDispatched, true);
    assert.equal(realmFailureNotice(error), `realm: ${stage}/${reason}\n${preparationNotice}`);
    return true;
  };
}

test('hidden identity preparation is one fixed write, independently verified before full fresh planning and convergent on rerun', async () => {
  const fixture = preparationProvider();
  const before = structuredClone(fixture.state);
  const recording = providerTransport(fixture);
  assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 1, preparationDispatched: true });
  const preparation = recording.requests.findIndex(request => request.stage === 'prepare' && request.method === 'PUT');
  assert.ok(preparation > 0);
  assert.deepEqual(recording.requests.filter(request => request.method && !request.form)
    .map(request => [request.stage, request.method, request.path]),
  [['prepare', 'PUT', '/admin/realms/learnstack/users/profile']]);
  assert.deepEqual(recording.requests.slice(preparation + 1, preparation + 4)
    .map(request => [request.stage, request.path]), [
    ['prepare', '/admin/realms/learnstack/users/profile'],
    ['plan', '/admin/realms/learnstack/events/config'],
    ['plan', '/admin/realms/learnstack/'],
  ]);
  assert.deepEqual(fixture.state.profile.attributes.slice(0, before.profile.attributes.length), before.profile.attributes);
  assert.equal(Object.hasOwn(fixture.state.profile, 'unmanagedAttributePolicy'), false);
  before.profile = structuredClone(fixture.state.profile);
  assert.deepEqual(fixture.state, before, 'metadata alone never changes existing user IDs, credentials or attributes');
  assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 0 });
  assert.equal(fixture.writes.length, 1);
});

test('already readable unmanaged identities use the ordinary profile path without preparation progress', async () => {
  for (const unmanagedAttributePolicy of ['ENABLED', 'ADMIN_EDIT', 'ADMIN_VIEW']) {
    const fixture = preparationProvider();
    fixture.state.profile.unmanagedAttributePolicy = unmanagedAttributePolicy;
    const recording = providerTransport(fixture);
    assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 1 });
    assert.equal(recording.requests.some(request => request.stage === 'prepare'), false);
    assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 0 });
  }
});

test('preparation and the independently authorized ordinary profile normalization remain two counted writes', async () => {
  const fixture = preparationProvider();
  const attribute = structuredClone(provider().state.profile.attributes.find(attribute => attribute.name === 'user_id'));
  attribute.permissions = { view: ['admin'], edit: [] };
  attribute.multivalued = true;
  fixture.state.profile.attributes.push(attribute);
  const recording = providerTransport(fixture);
  assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 2, preparationDispatched: true });
  const writes = recording.requests.filter(request => request.method === 'PUT');
  assert.deepEqual(writes.map(request => [request.stage, request.path]), [
    ['prepare', '/admin/realms/learnstack/users/profile'], ['apply', '/admin/realms/learnstack/users/profile'],
  ]);
  assert.deepEqual(writes[0].json.attributes.find(value => value.name === 'user_id'), attribute);
  assert.deepEqual(writes[1].json.attributes.find(value => value.name === 'user_id').permissions,
    { view: ['admin'], edit: ['admin'] });
  const prepare = recording.requests.indexOf(writes[0]);
  const apply = recording.requests.indexOf(writes[1]);
  assert.ok(recording.requests.slice(prepare + 1, apply).some(request => request.stage === 'plan'
    && request.path.startsWith('/admin/realms/learnstack/users?')),
  'the ordinary profile action comes only from a complete fresh snapshot');
  assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 0 });
});

for (const [status, bytes, reason, description] of [
  [204, '', 'status', 'HTTP 204'], [201, '{}', 'status', 'HTTP 201'],
  [403, '{}', 'status', 'HTTP 403'], [500, privateCanary, 'status', 'HTTP 500'],
  [200, '{', 'response', 'malformed JSON'], [200, '', 'response', 'empty JSON'],
  [200, 'null', 'response', 'null JSON'], [200, '[]', 'response', 'array JSON'],
]) {
  test(`preparation ${description} refusal stops after its dispatched metadata call`, async () => {
    const fixture = preparationProvider();
    const recording = providerTransport(fixture);
    const transport = async (url, options) => {
      const response = await recording.transport(url, options);
      return options.stage === 'prepare' && options.method === 'PUT'
        ? { status, bytes: Buffer.from(bytes) } : response;
    };
    await assert.rejects(reconcileRealm(memoryInput(transport)), preparationFailure('prepare', reason));
    assert.equal(fixture.writes.length, 1);
    assert.equal(recording.requests.filter(request => request.stage === 'prepare').length, 1);
    assert.equal(recording.requests.some(request => request.stage === 'apply'), false);
    assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 0 });
  });
}

test('separate preparation readback rejects any full-profile field or ordered-array drift before ordinary work', async () => {
  for (const change of [
    profile => { profile.attributes.reverse(); },
    profile => { profile.groups = [{ name: 'unexpected-group' }]; },
    profile => { profile.unmanagedAttributePolicy = 'ADMIN_VIEW'; },
    profile => { profile.attributes.at(-1).annotations = { unexpected: privateCanary }; },
  ]) {
    const fixture = preparationProvider();
    const recording = providerTransport(fixture);
    const transport = async (url, options) => {
      const response = await recording.transport(url, options);
      if (options.stage === 'prepare' && !options.method) {
        const profile = JSON.parse(response.bytes);
        change(profile);
        return { status: 200, bytes: Buffer.from(JSON.stringify(profile)) };
      }
      return response;
    };
    await assert.rejects(reconcileRealm(memoryInput(transport)), preparationFailure('prepare', 'readback_conflict'));
    assert.equal(fixture.writes.length, 1);
    assert.equal(recording.requests.some(request => request.stage === 'apply'), false);
  }
});

test('a preparation acknowledgement loss or cancellation retains durable progress without replay or ordinary writes', async () => {
  for (const outcome of ['lost_ack', 'cancelled']) {
    const fixture = preparationProvider();
    const recording = providerTransport(fixture);
    const parent = new AbortController();
    const transport = async (url, options) => {
      const response = await recording.transport(url, options);
      if (options.stage === 'prepare' && options.method === 'PUT') {
        if (outcome === 'lost_ack') throw new Error(privateCanary);
        parent.abort(new Error(privateCanary));
      }
      return response;
    };
    const input = { ...memoryInput(transport), signal: parent.signal };
    await assert.rejects(reconcileRealm(input), preparationFailure('prepare', outcome === 'lost_ack' ? 'response' : 'cancelled'));
    assert.equal(fixture.writes.length, 1);
    assert.equal(recording.requests.filter(request => request.stage === 'prepare').length, 1);
    assert.equal(recording.requests.some(request => request.stage === 'apply'), false);
    assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 0 });
  }
});

test('whole-pass timeout after preparation dispatch preserves closed deadline progress', async t => {
  const fixture = preparationProvider();
  const recording = providerTransport(fixture);
  const deadline = new AbortController();
  t.mock.method(AbortSignal, 'timeout', () => deadline.signal);
  const transport = async (url, options) => {
    const response = await recording.transport(url, options);
    if (options.stage === 'prepare' && options.method === 'PUT') deadline.abort(new Error(privateCanary));
    return response;
  };
  await assert.rejects(reconcileRealm(memoryInput(transport)), preparationFailure('prepare', 'deadline'));
  assert.equal(fixture.writes.length, 1);
  assert.equal(recording.requests.some(request => request.stage === 'apply'), false);
});

test('newly exposed actor conflict and identity-independent drift leave only preparation durable before a fresh-plan refusal', async () => {
  for (const kind of ['hidden_actor', 'realm_drift']) {
    const fixture = preparationProvider();
    if (kind === 'hidden_actor') fixture.state.users[0].attributes.user_id = ['01910000-0000-7000-8000-00000000ffff'];
    fixture.state.realm.accessTokenLifespan = 1800;
    const before = structuredClone(fixture.state);
    const recording = providerTransport(fixture);
    const transport = async (url, options) => {
      const response = await recording.transport(url, options);
      if (kind === 'realm_drift' && options.stage === 'prepare' && options.method === 'PUT')
        fixture.state.realm.enabled = false;
      return response;
    };
    await assert.rejects(reconcileRealm(memoryInput(transport)), preparationFailure('plan', kind === 'hidden_actor' ? 'identity_conflict' : 'unsupported'));
    assert.deepEqual(fixture.state.users, before.users);
    assert.equal(fixture.state.realm.accessTokenLifespan, 1800, 'the earlier qualified ordinary repair was discarded');
    assert.equal(fixture.writes.length, 1);
    assert.equal(recording.requests.some(request => request.stage === 'apply'), false);
  }
});

test('preparation progress begins only after proactive credential refresh and pre-dispatch cancellation admission', async t => {
  const probe = providerTransport(preparationProvider());
  await reconcileRealm(memoryInput(probe.transport));
  const handoff = probe.requests.findIndex(request => request.stage === 'prepare' && request.method === 'PUT');
  assert.ok(handoff > 0);
  let now = 0;
  t.mock.method(performance, 'now', () => now);
  for (const outcome of ['refresh_failure', 'cancelled']) {
    now = 0;
    const fixture = preparationProvider();
    const recording = providerTransport(fixture);
    const stop = new AbortController();
    let calls = 0;
    let refreshes = 0;
    const transport = async (url, options) => {
      calls++;
      if (options.form?.grant_type === 'refresh_token') {
        refreshes++;
        return { status: 401, bytes: Buffer.from('{}') };
      }
      const response = await recording.transport(url, options);
      if (calls === handoff) {
        if (outcome === 'refresh_failure') now = 56000;
        else stop.abort(new Error(privateCanary));
      }
      return response;
    };
    await assert.rejects(reconcileRealm({ ...memoryInput(transport), signal: stop.signal }), error => {
      refusal(outcome === 'refresh_failure' ? 'authentication' : 'plan', outcome === 'refresh_failure' ? 'status' : 'cancelled')(error);
      assert.equal(error.preparationDispatched, undefined);
      assert.equal(realmFailureNotice(error).includes(preparationNotice), false);
      return true;
    });
    assert.equal(refreshes, outcome === 'refresh_failure' ? 1 : 0);
    assert.equal(fixture.writes.length, 0);
    assert.equal(recording.requests.some(request => request.stage === 'prepare'), false);
  }
});

test('preparation readback HTTP or JSON faults retain metadata progress and prevent fresh planning', async () => {
  for (const [status, bytes, reason] of [[403, '{}', 'status'], [500, privateCanary, 'status'], [200, '{', 'response']]) {
    const fixture = preparationProvider();
    const recording = providerTransport(fixture);
    const transport = async (url, options) => {
      const response = await recording.transport(url, options);
      return options.stage === 'prepare' && !options.method ? { status, bytes: Buffer.from(bytes) } : response;
    };
    await assert.rejects(reconcileRealm(memoryInput(transport)), preparationFailure('prepare', reason));
    const preparation = recording.requests.findIndex(request => request.stage === 'prepare');
    assert.equal(recording.requests.slice(preparation + 1).some(request => request.stage === 'plan'), false);
    assert.equal(fixture.writes.length, 1);
  }
});

test('uncertain preparation transport handoff never reports an undispatched pass even without a receipt or stored mutation', async () => {
  const fixture = preparationProvider();
  const recording = providerTransport(fixture);
  let dispatches = 0;
  const transport = async (url, options) => {
    if (options.stage === 'prepare' && options.method === 'PUT') {
      dispatches++;
      throw new RealmError('prepare', 'transport');
    }
    return recording.transport(url, options);
  };
  await assert.rejects(reconcileRealm(memoryInput(transport)), preparationFailure('prepare', 'transport'));
  assert.equal(dispatches, 1);
  assert.equal(fixture.writes.length, 0);
  assert.equal(recording.requests.some(request => request.stage === 'apply'), false);
});

for (const stage of ['apply', 'readback']) {
  test(`a later ${stage} failure preserves earlier dispatched preparation and explicit reruns use current state`, async () => {
    const fixture = preparationProvider();
    fixture.state.realm.accessTokenLifespan = 1800;
    const recording = providerTransport(fixture);
    const transport = async (url, options) => {
      const response = await recording.transport(url, options);
      if (options.stage === stage) throw new Error(privateCanary);
      return response;
    };
    await assert.rejects(reconcileRealm(memoryInput(transport)), preparationFailure(stage, 'response'));
    assert.equal(fixture.writes.length, 2);
    assert.equal(fixture.state.realm.accessTokenLifespan, 900);
    assert.deepEqual(await reconcileRealm(memoryInput(recording.transport)), { mutations: 0 });
    assert.equal(fixture.writes.length, 2);
  });
}
