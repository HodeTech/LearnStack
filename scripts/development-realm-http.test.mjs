import assert from 'node:assert/strict';
import dns from 'node:dns';
import { once } from 'node:events';
import http from 'node:http';
import test from 'node:test';
import { gzipSync } from 'node:zlib';
import { RealmError, realmFailure } from './development-realm-errors.mjs';
import {
  realmBodyBytes,
  realmHttpMilliseconds,
  realmJson,
  realmRequest,
} from './development-realm-http.mjs';

const canary = 'synthetic-private-canary-7fa6';

async function endpoint(t, handler) {
  const sockets = new Set();
  const socketClosures = [];
  const requests = [];
  const server = http.createServer((request, response) => {
    requests.push(request);
    response.setHeader('Content-Type', 'application/json');
    handler(request, response);
  });
  server.on('connection', (socket) => {
    sockets.add(socket);
    socketClosures.push(new Promise(resolve => socket.once('close', () => {
      sockets.delete(socket);
      resolve();
    })));
  });
  t.after(async () => {
    const closed = new Promise((resolve, reject) => {
      server.close((error) => (error ? reject(error) : resolve()));
    });
    for (const socket of sockets) socket.destroy();
    await closed;
    await Promise.all(socketClosures);
    assert.equal(sockets.size, 0);
  });
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  return {
    url: new URL(`http://127.0.0.1:${server.address().port}/`),
    requests,
    sockets,
  };
}

function refusal(stage, reason) {
  return (error) => {
    assert.ok(error instanceof RealmError);
    assert.equal(error.stage, stage);
    assert.equal(error.reason, reason);
    assert.equal(realmFailure(error), `realm: ${stage}/${reason}`);
    assert.ok(!JSON.stringify(error).includes(canary));
    assert.ok(!error.message.includes(canary));
    assert.ok(!error.stack.includes(canary));
    return true;
  };
}

function observeExchanges(t, onResponse) {
  const original = http.request;
  const exchanges = [];
  http.request = (...args) => {
    const request = original(...args);
    const exchange = { request };
    const destroyAgent = request.agent.destroy.bind(request.agent);
    request.agent.destroy = () => {
      exchange.agentDestroyed = true;
      destroyAgent();
    };
    exchanges.push(exchange);
    request.once('socket', (socket) => { exchange.socket = socket; });
    request.once('response', (response) => {
      exchange.response = response;
      onResponse?.();
    });
    return request;
  };
  t.after(() => { http.request = original; });
  return exchanges;
}

function assertClosed(exchanges) {
  assert.equal(exchanges.length, 1, 'a refused exchange must never retry');
  const { request, response, socket } = exchanges[0];
  assert.equal(exchanges[0].agentDestroyed, true);
  assert.equal(request.closed, true);
  assert.equal(request.destroyed, true);
  assert.equal(socket.destroyed, true);
  if (response) {
    assert.equal(response.closed, true);
    assert.equal(response.destroyed, true);
  }
  assert.equal(Object.values(request.agent.sockets).flat().length, 0);
  assert.equal(Object.values(request.agent.freeSockets).flat().length, 0);
}

async function remoteClosure(promise) {
  let timer;
  try {
    await Promise.race([
      promise,
      new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error('remote socket must close after local cancellation')), 1000);
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}

test('successful JSON and form requests use the actual bounded identity transport', async (t) => {
  const bodies = [];
  const f = await endpoint(t, (request, response) => {
    const chunks = [];
    request.on('data', (chunk) => chunks.push(chunk));
    request.once('end', () => {
      bodies.push(Buffer.concat(chunks).toString('utf8'));
      response.writeHead(200, { 'Content-Type': 'application/json', 'Content-Encoding': 'identity' });
      response.end('{"ok":true}');
    });
  });
  for (const options of [
    { json: { label: 'owned' } },
    { form: { username: 'synthetic-admin', password: canary } },
  ]) {
    const result = await realmRequest(f.url, { method: 'POST', bearer: canary, ...options });
    assert.equal(result.status, 200);
    assert.deepEqual(realmJson(result, 'plan'), { ok: true });
  }
  assert.deepEqual(bodies, [
    '{"label":"owned"}',
    `username=synthetic-admin&password=${canary}`,
  ]);
  assert.equal(f.requests.length, 2);
  for (const request of f.requests) {
    assert.equal(request.method, 'POST');
    assert.equal(request.headers['accept-encoding'], 'identity');
    assert.equal(request.headers.authorization, `Bearer ${canary}`);
    assert.ok(!request.url.includes(canary));
  }
  assert.equal(f.requests[0].headers['content-type'], 'application/json');
  assert.equal(f.requests[1].headers['content-type'], 'application/x-www-form-urlencoded');
});

for (const contentType of ['text/html', 'text/plain', 'application/octet-stream', 'application/jsonx']) {
  test(`a successful non-JSON ${contentType} response is refused after actual exchange cleanup`, async t => {
    const exchanges = observeExchanges(t);
    const f = await endpoint(t, (_request, response) => {
      response.setHeader('Content-Type', contentType);
      response.end('{}');
    });
    await assert.rejects(realmRequest(f.url), refusal('plan', 'response'));
    assertClosed(exchanges);
    assert.equal(f.requests.length, 1);
  });
}

test('localhost connects only to pinned IPv4 loopback without system DNS and retains its Host', async (t) => {
  let host;
  const f = await endpoint(t, (request, response) => {
    host = request.headers.host;
    response.end('{"issuer":"localhost"}');
  });
  const url = new URL(f.url);
  url.hostname = 'localhost';
  const originalLookup = dns.lookup;
  const originalRequest = http.request;
  let systemLookups = 0;
  let requestUrl;
  const lookups = [];
  let peer;
  dns.lookup = (...args) => {
    systemLookups++;
    args.at(-1)(new Error('system DNS must not select the issuer address'));
  };
  http.request = (target, options) => {
    requestUrl = target.href;
    const suppliedLookup = options.lookup;
    const observed = typeof suppliedLookup === 'function' ? {
      ...options,
      lookup: (hostname, lookupOptions, callback) => suppliedLookup(hostname, lookupOptions, (...result) => {
        lookups.push({ hostname, result });
        callback(...result);
      }),
    } : options;
    const request = originalRequest(target, observed);
    request.once('socket', (socket) => {
      socket.once('connect', () => {
        peer = { address: socket.remoteAddress, family: socket.remoteFamily, port: socket.remotePort };
      });
    });
    return request;
  };
  try {
    const result = await realmRequest(url, { bearer: canary });
    assert.deepEqual(realmJson(result, 'plan'), { issuer: 'localhost' });
    assert.equal(result.status, 200);
    assert.equal(systemLookups, 0);
    assert.equal(requestUrl, url.href);
    assert.equal(host, `localhost:${f.url.port}`);
    assert.deepEqual(peer, { address: '127.0.0.1', family: 'IPv4', port: Number(f.url.port) });
    assert.equal(lookups.length, 1);
    assert.equal(lookups[0].hostname, 'localhost');
    const [error, address, family] = lookups[0].result;
    assert.equal(error, null);
    if (Array.isArray(address)) assert.deepEqual(address, [{ address: '127.0.0.1', family: 4 }]);
    else {
      assert.equal(address, '127.0.0.1');
      assert.equal(family, 4);
    }
    assert.equal(f.requests.length, 1);
    assert.equal(f.requests[0].headers.authorization, `Bearer ${canary}`);
  } finally {
    dns.lookup = originalLookup;
    http.request = originalRequest;
  }
});

for (const stage of ['discovery', 'authentication', 'plan', 'apply']) {
  test(`${stage} refuses redirects without following or forwarding credentials`, async (t) => {
    const destination = await endpoint(t, (_request, response) => response.end('{}'));
    const f = await endpoint(t, (_request, response) => {
      response.writeHead(302, { Location: new URL(canary, destination.url).href });
      response.end(canary);
    });
    await assert.rejects(
      realmRequest(f.url, { stage, method: 'POST', form: { password: canary }, bearer: canary }),
      refusal(stage, 'redirect'),
    );
    assert.equal(f.requests.length, 1);
    assert.equal(destination.requests.length, 0);
    assert.equal(destination.sockets.size, 0);
  });
}

test('a response at 256 KiB succeeds and one byte above the bound is refused', async (t) => {
  assert.equal(realmBodyBytes, 256 * 1024);
  const exact = Buffer.from(`"${'x'.repeat(256 * 1024 - 2)}"`);
  const f = await endpoint(t, (request, response) => {
    response.writeHead(200, { 'Content-Type': 'application/json' });
    response.end(request.url === '/exact' ? exact : Buffer.concat([exact, Buffer.from(' ')]));
  });
  const result = await realmRequest(new URL('exact', f.url));
  assert.equal(result.bytes.length, 256 * 1024);
  assert.equal(realmJson(result, 'plan').length, 256 * 1024 - 2);
  await assert.rejects(realmRequest(new URL('over', f.url)), refusal('plan', 'body_limit'));
  assert.equal(f.requests.length, 2);
});

test('chunked wire bytes cannot evade the response bound without Content-Length', async (t) => {
  const f = await endpoint(t, (_request, response) => {
    response.writeHead(200);
    response.write(Buffer.alloc(128 * 1024, 32));
    response.write(Buffer.alloc(128 * 1024, 32));
    response.end(canary);
  });
  await assert.rejects(realmRequest(f.url), refusal('plan', 'body_limit'));
  assert.equal(f.requests.length, 1);
});

test('only the fixed authenticated successful server-information read accepts exactly 512 KiB', async (t) => {
  const exact = Buffer.from(JSON.stringify({ systemInfo: { version: '26.6.2' }, padding: '' }));
  const body = Buffer.concat([exact, Buffer.alloc(512 * 1024 - exact.length, 32)]);
  const f = await endpoint(t, (_request, response) => response.end(body));
  const result = await realmRequest(new URL('admin/serverinfo', f.url), { bearer: canary, stage: 'version' });
  assert.equal(result.bytes.length, 512 * 1024);
  assert.equal(realmJson(result, 'version').systemInfo.version, '26.6.2');
  assert.equal(f.requests.length, 1);
  assert.equal(f.requests[0].headers.authorization, `Bearer ${canary}`);
});

test('server information refuses 512 KiB plus one byte and closes the actual exchange', async (t) => {
  const exchanges = observeExchanges(t);
  const f = await endpoint(t, (_request, response) => {
    response.writeHead(200);
    response.write(Buffer.alloc(256 * 1024, 32));
    response.end(Buffer.alloc(256 * 1024 + 1, 32));
  });
  await assert.rejects(realmRequest(new URL('admin/serverinfo', f.url), { bearer: canary, stage: 'version' }),
    refusal('version', 'body_limit'));
  assertClosed(exchanges);
});

for (const [name, path, options, status] of [
  ['ordinary path', 'admin/realms/learnstack', { bearer: canary }, 200],
  ['path suffix', 'admin/serverinfo/', { bearer: canary }, 200],
  ['encoded path', 'admin/%73erverinfo', { bearer: canary }, 200],
  ['query', 'admin/serverinfo?fields=systemInfo.version', { bearer: canary }, 200],
  ['missing bearer', 'admin/serverinfo', {}, 200],
  ['empty bearer', 'admin/serverinfo', { bearer: '' }, 200],
  ['POST', 'admin/serverinfo', { method: 'POST', bearer: canary }, 200],
  ['GET with JSON body', 'admin/serverinfo', { json: {}, bearer: canary }, 200],
  ['GET with empty form', 'admin/serverinfo', { form: {}, bearer: canary }, 200],
  ['unauthorized', 'admin/serverinfo', { bearer: canary }, 401],
  ['forbidden', 'admin/serverinfo', { bearer: canary }, 403],
  ['not found', 'admin/serverinfo', { bearer: canary }, 404],
  ['partial content', 'admin/serverinfo', { bearer: canary }, 206],
  ['upstream failure', 'admin/serverinfo', { bearer: canary }, 500],
]) {
  test(`the ${name} case retains 256 KiB despite a version stage or caller-supplied larger limit`, async (t) => {
    const f = await endpoint(t, (request, response) => {
      request.resume();
      response.writeHead(status);
      response.end(Buffer.alloc(256 * 1024 + 1, 32));
    });
    await assert.rejects(realmRequest(new URL(path, f.url), {
      stage: 'version', responseBytes: 1024 * 1024, ...options,
    }), refusal('version', 'body_limit'));
    assert.equal(f.requests.length, 1);
  });
}

test('the server-information exception cannot widen any outbound body', async (t) => {
  const f = await endpoint(t, (_request, response) => response.end('{}'));
  for (const payload of [
    { json: { value: 'x'.repeat(256 * 1024) } },
    { form: { value: 'x'.repeat(256 * 1024) } },
  ]) assert.throws(() => realmRequest(new URL('admin/serverinfo', f.url), {
    bearer: canary, stage: 'version', ...payload,
  }), refusal('version', 'body_limit'));
  assert.equal(f.requests.length, 0);
});

for (const encoding of ['gzip', 'br', 'deflate', 'gzip, identity']) {
  test(`${encoding} is refused so compression cannot widen the decoded response bound`, async (t) => {
    const bomb = gzipSync(Buffer.alloc(256 * 1024 + 1, 32));
    assert.ok(bomb.length < 256 * 1024);
    const f = await endpoint(t, (_request, response) => {
      response.writeHead(200, { 'Content-Encoding': encoding });
      response.end(bomb);
    });
    await assert.rejects(realmRequest(f.url), refusal('plan', 'response'));
    assert.equal(f.requests.length, 1);
    assert.equal(f.requests[0].headers['accept-encoding'], 'identity');
  });
}

test('malformed upstream JSON becomes only the closed stage and reason', async (t) => {
  const f = await endpoint(t, (_request, response) => response.end(`{"token":"${canary}"`));
  const result = await realmRequest(f.url, { stage: 'authentication' });
  assert.throws(() => realmJson(result, 'authentication'), refusal('authentication', 'response'));
  assert.equal(f.requests.length, 1);
});

test('oversized outbound JSON and forms are refused before connecting', async (t) => {
  const f = await endpoint(t, (_request, response) => response.end('{}'));
  for (const options of [
    { json: { value: 'x'.repeat(256 * 1024) } },
    { form: { password: 'x'.repeat(256 * 1024) } },
  ]) assert.throws(() => realmRequest(f.url, options), refusal('plan', 'body_limit'));
  assert.equal(f.requests.length, 0);
  assert.equal(f.sockets.size, 0);
});

test('pre-aborted calls and invalid credential-bearing URLs never connect', async (t) => {
  const f = await endpoint(t, (_request, response) => response.end('{}'));
  const credentials = new URL(f.url);
  credentials.username = canary;
  credentials.password = canary;
  assert.throws(() => realmRequest(credentials), refusal('plan', 'configuration'));
  assert.throws(
    () => realmRequest(f.url, { signal: AbortSignal.abort(new Error(canary)) }),
    refusal('plan', 'cancelled'),
  );
  assert.equal(f.requests.length, 0);
  assert.equal(f.sockets.size, 0);
});

test('an abort closes owned HTTP resources before settling and the server observes closure', async (t) => {
  const controller = new AbortController();
  const exchanges = observeExchanges(t, () => queueMicrotask(() => controller.abort(new Error(canary))));
  let closed;
  const f = await endpoint(t, (request, response) => {
    closed = new Promise((resolve) => request.socket.once('close', resolve));
    response.writeHead(200);
    response.write('{');
  });
  await assert.rejects(realmRequest(f.url, { signal: controller.signal }), refusal('plan', 'cancelled'));
  assert.throws(() => realmRequest(f.url, { signal: controller.signal }), refusal('plan', 'cancelled'));
  assert.ok(exchanges[0].response, 'the abort must exercise an active response body');
  assertClosed(exchanges);
  await remoteClosure(closed);
  assert.equal(f.requests.length, 1);
  assert.equal(f.sockets.size, 0);
});

for (const streamBody of [false, true]) {
  test(`the absolute five-second deadline closes a ${streamBody ? 'trickling' : 'silent'} response`, async (t) => {
    const exchanges = observeExchanges(t);
    assert.equal(realmHttpMilliseconds, 5000);
    let closed;
    let writes = 0;
    const f = await endpoint(t, (request, response) => {
      closed = new Promise((resolve) => request.socket.once('close', resolve));
      if (streamBody) {
        response.writeHead(200);
        response.write('{');
        const interval = setInterval(() => { writes++; response.write(' '); }, 50);
        request.socket.once('close', () => clearInterval(interval));
        t.after(() => clearInterval(interval));
      }
    });
    const started = performance.now();
    await assert.rejects(realmRequest(f.url), refusal('plan', 'deadline'));
    const elapsed = performance.now() - started;
    assert.ok(elapsed >= 4800 && elapsed < 7500, 'deadline must be absolute and bounded');
    assertClosed(exchanges);
    await remoteClosure(closed);
    assert.equal(f.requests.length, 1);
    assert.equal(f.sockets.size, 0);
    if (streamBody) assert.ok(writes >= 5, 'a progressing response must not reset the deadline');
  });
}

test('a severed connection produces a closed transport failure with no retry', async (t) => {
  const f = await endpoint(t, (request) => request.socket.destroy(new Error(canary)));
  await assert.rejects(realmRequest(f.url, { bearer: canary }), refusal('plan', 'transport'));
  assert.equal(f.requests.length, 1);
});

test('diagnostics refuse arbitrary upstream exceptions, credentials, claims and URLs', () => {
  for (const value of [
    new Error(`http://user:${canary}@example.invalid/token?access_token=${canary}`),
    { message: canary, body: canary, access_token: canary },
    canary,
    null,
  ]) assert.equal(realmFailure(value), 'realm: preflight/configuration');
  const error = new RealmError(canary, canary);
  assert.ok(refusal('preflight', 'configuration')(error));
  assert.equal(realmFailure(new RealmError('cleanup', 'cleanup')), 'realm: cleanup/cleanup');
});
