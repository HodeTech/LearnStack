// One owned loopback HTTP request at a time; the ordinary client ceiling is unchanged.
import assert from 'node:assert/strict';
import { request } from 'node:http';
import { gunzipSync } from 'node:zlib';

import { faultCases } from './framework-router-fault-gate.mjs';

export function requestRouterFault({ port, name }) {
  assert.ok(faultCases.includes(name));
  assert.ok(Number.isSafeInteger(port) && port > 0 && port <= 65535);
  let call,
    peer,
    abortRequested = false,
    responseCount = 0,
    settled = false,
    failed = false;
  const chunks = [];
  let size = 0;
  let status = null,
    encoding = null,
    cache = null,
    ended = false;
  const completed = new Promise((resolve, reject) => {
    const fail = () => {
      if (settled) return;
      failed = true;
      settled = true;
      reject(new Error('Copied fault client failed'));
    };
    const finish = () => {
      if (settled) return;
      if (failed) return fail();
      settled = true;
      resolve(
        Object.freeze({
          status,
          encoding,
          cache,
          responseCount,
          ended,
          abortRequested,
          wireBytes: size,
          body: Buffer.concat(chunks),
        }),
      );
    };
    call = request(
      {
        hostname: '127.0.0.1',
        port,
        method: 'GET',
        path: '/en/courses/transport',
        agent: false,
        headers: {
          host: 'institution.example.test:3000',
          accept: 'text/plain',
          'accept-encoding': 'gzip',
        },
      },
      (response) => {
        if (++responseCount !== 1) {
          fail();
          response.destroy();
          return;
        }
        peer = response;
        status = response.statusCode;
        const value = response.headers['content-encoding'];
        encoding = value === undefined ? null : value === 'gzip' ? 'gzip' : 'other';
        cache = response.headers['cache-control'] === 'no-store' ? 'no-store' : 'other';
        response.on('data', (chunk) => {
          size += chunk.length;
          if (size > 65536) {
            fail();
            call.destroy();
            response.destroy();
          } else chunks.push(chunk);
        });
        response.once('end', () => {
          ended = true;
        });
        response.once('error', (error) => {
          if (error?.code !== 'ECONNRESET') fail();
        });
        response.once('close', finish);
      },
    );
    call.setTimeout(10_000, () => {
      fail();
      call.destroy();
    });
    call.once('error', (error) => {
      if (error?.code !== 'ECONNRESET') fail();
    });
    call.once('close', () => {
      if (!peer) finish();
    });
    call.end();
  });
  void completed.catch(() => {});
  return Object.freeze({
    completed,
    abort() {
      assert.equal(name, 'abort-before-selection');
      assert.equal(abortRequested, false);
      assert.equal(settled, false);
      abortRequested = true;
      call.destroy();
    },
    cancel() {
      call?.destroy();
      peer?.destroy();
    },
  });
}

export function validateRouterFaultWire(value, name) {
  assert.ok(faultCases.includes(name));
  assert.ok([null, 200, 500, 503].includes(value.status));
  assert.ok([null, 'gzip'].includes(value.encoding));
  assert.ok([0, 1].includes(value.responseCount));
  assert.ok(value.wireBytes >= 0 && value.wireBytes <= 65536);
  assert.equal(Buffer.isBuffer(value.body), true);
  assert.equal(value.body.length, value.wireBytes);
  if (name === 'finish-cleanup-fault-gzip') {
    assert.equal(value.ended, true);
    assert.equal(value.responseCount, 1);
    assert.equal(value.status, 200);
    assert.equal(value.encoding, 'gzip');
    assert.equal(value.cache, 'no-store');
    assert.deepEqual(gunzipSync(value.body), Buffer.alloc(4096, 65));
  } else if (name === 'abort-before-selection') {
    assert.equal(value.abortRequested, true);
    assert.equal(value.responseCount, 0);
    assert.equal(value.status, null);
    assert.equal(value.wireBytes, 0);
    assert.equal(value.ended, false);
  } else {
    assert.equal(value.abortRequested, false);
    if (name !== 'pipe-before-selection') assert.equal(value.ended, false);
  }
  return Object.freeze({
    status: value.status,
    encoding: value.encoding,
    responseCount: value.responseCount,
    ended: value.ended,
    abortRequested: value.abortRequested,
    wireBytes: value.wireBytes,
  });
}
