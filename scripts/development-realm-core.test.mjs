import assert from 'node:assert/strict';
import { once } from 'node:events';
import { readFile } from 'node:fs/promises';
import http from 'node:http';
import test from 'node:test';
import { reconcileRealm } from './development-realm-core.mjs';
import { RealmError, realmFailure } from './development-realm-errors.mjs';

const privateCanary = 'synthetic-private-token-and-password-41ae';

async function fixture(t, { discovery, authentication, version, desired = {}, manifest = {} } = {}) {
  const paths = [];
  const sockets = new Set();
  let base;
  const server = http.createServer((request, response) => {
    paths.push([request.method, request.url]);
    response.setHeader('Content-Type', 'application/json');
    if (request.url === '/realms/learnstack/.well-known/openid-configuration') {
      response.end(JSON.stringify({ issuer: discovery ?? new URL('realms/learnstack', base).href }));
    } else if (request.url === '/realms/master/protocol/openid-connect/token') {
      request.resume();
      response.statusCode = authentication?.status ?? 200;
      response.end(authentication?.body ?? JSON.stringify({ access_token: privateCanary }));
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
    userId: user.attributes.user_id[0], tenantId: user.attributes.tenant_id[0],
    organizationId: user.attributes.organization_id?.[0] ?? null })) };
  const f = await fixture(t, { version: { body }, desired, manifest });
  // This deliberately has no realm state: reaching the first read witnesses
  // accepted version admission without inventing a successful reconciliation.
  await assert.rejects(f.invoke(), refusal('plan', 'status'));
  assert.equal(f.paths.length, 4);
  assert.deepEqual(f.paths[2], ['GET', '/admin/serverinfo']);
  assert.deepEqual(f.paths[3], ['GET', '/admin/realms/learnstack/']);
});
