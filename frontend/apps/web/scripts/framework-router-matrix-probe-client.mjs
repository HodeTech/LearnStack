// Fixed three-request client: one unseeded identity baseline, then two conditional encoder cases.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { join } from 'node:path';
import { gunzipSync, inflateSync } from 'node:zlib';

import { copiedTransportEventsComplete } from './framework-transport-proof-host.mjs';

const TARGET = '/en/courses/transport';
const HOST = 'institution.example.test:3000';
const BODY = Buffer.alloc(8192, 65);
const EXPECTED = Object.freeze([
  Object.freeze({
    transport: 'https',
    encoding: 'gzip',
    name: 'healthy-identity',
    responseEncoding: null,
    precondition: 'unseeded-native-header',
  }),
  Object.freeze({
    transport: 'https',
    encoding: 'gzip',
    name: 'healthy-gzip',
    responseEncoding: 'gzip',
    precondition: 'native-string-content-type',
  }),
  Object.freeze({
    transport: 'http',
    encoding: 'deflate',
    name: 'healthy-deflate',
    responseEncoding: 'deflate',
    precondition: 'native-string-content-type',
  }),
]);

/** The host owns both actual sockets; the existing 10-second client ceiling applies. */
export function requestCopiedRoute({ root, transport, port, encoding, name }) {
  if (
    !EXPECTED.some(
      (item) => item.name === name && item.transport === transport && item.encoding === encoding,
    ) ||
    !Number.isSafeInteger(port) ||
    port < 1 ||
    port > 65535
  )
    throw new Error('Copied router request refused');
  return new Promise((resolve, reject) => {
    const call = (transport === 'https' ? httpsRequest : httpRequest)(
      {
        hostname: '127.0.0.1',
        port,
        method: 'GET',
        path: TARGET,
        agent: false,
        ...(transport === 'https'
          ? {
              servername: 'localhost',
              ca: readFileSync(join(root, 'cert.pem')),
            }
          : {}),
        headers: {
          host: HOST,
          accept: 'text/plain',
          'accept-encoding': encoding,
        },
      },
      (response) => {
        const authorized = transport === 'https' ? response.socket.authorized === true : null;
        const chunks = [];
        let size = 0;
        response.on('data', (chunk) => {
          size += chunk.length;
          if (size > 2 * 1024 * 1024) {
            call.destroy(new Error('Owned router response body limit'));
            response.destroy();
          } else chunks.push(chunk);
        });
        response.once('error', reject);
        response.once('aborted', () => reject(new Error('Owned router response aborted')));
        response.once('end', () =>
          resolve({
            status: response.statusCode,
            headers: response.headers,
            body: Buffer.concat(chunks),
            authorized,
          }),
        );
      },
    );
    call.setTimeout(10_000, () => call.destroy(new Error('Owned router request deadline')));
    call.once('error', reject);
    call.end();
  });
}

/** This validates actual peer bytes; it never substitutes an encoder or response event. */
export function validateCopiedRouteWire(result, name) {
  const selected = EXPECTED.find((item) => item.name === name);
  assert.ok(selected, 'Closed copied router case');
  assert.equal(result.status, 200);
  assert.equal(result.headers['content-encoding'], selected.responseEncoding ?? undefined);
  assert.equal(result.headers['cache-control'], 'no-store');
  assert.match(result.headers['content-type'] ?? '', /^text\/plain(?:; charset=utf-8)?$/i);
  if (selected.transport === 'https') assert.equal(result.authorized, true);
  const body =
    selected.responseEncoding === null
      ? result.body
      : selected.responseEncoding === 'gzip'
        ? gunzipSync(result.body)
        : inflateSync(result.body);
  assert.deepEqual(body, BODY);
  return Object.freeze({
    case: name,
    transport: selected.transport,
    encoding: selected.responseEncoding ?? 'identity',
    precondition: selected.precondition,
    status: result.status,
    wireBytes: result.body.length,
    decodedBytes: body.length,
  });
}

/** Keep the existing strict transport event codec; require one additional exact pipe witness. */
export function validateCopiedRouteEvents({ rows, pipeRows, name, pipeUnion }) {
  assert.ok(copiedTransportEventsComplete(name, rows), 'Real router events complete');
  const kinds = rows.map((row) => row.kind);
  const baseline = name === 'healthy-identity';
  for (const kind of [
    'router',
    baseline ? 'none' : 'allocation',
    ...(baseline ? [] : ['encoder-close']),
    'response-finish',
    'response-close',
    'handler-ok',
  ])
    assert.equal(kinds.filter((value) => value === kind).length, 1, 'One actual ' + kind);
  if (baseline) {
    assert.equal(kinds.includes('allocation'), false);
    assert.equal(kinds.includes('encoder-close'), false);
    assert.ok(kinds.indexOf('router') < kinds.indexOf('none'));
    assert.equal(rows.find((row) => row.kind === 'none').value, 'filtered');
  } else {
    assert.equal(kinds.includes('none'), false);
    assert.ok(kinds.indexOf('router') < kinds.indexOf('allocation'));
    assert.ok(kinds.indexOf('allocation') < kinds.indexOf('encoder-close'));
    assert.ok(kinds.indexOf('encoder-close') < kinds.indexOf('handler-ok'));
    assert.equal(
      rows.find((row) => row.kind === 'allocation').value,
      name.endsWith('gzip') ? 'gzip' : 'deflate',
    );
    assert.equal(rows.find((row) => row.kind === 'encoder-close').value, 'ended');
  }
  assert.ok(kinds.indexOf('response-finish') < kinds.indexOf('response-close'));
  assert.ok(Object.isFrozen(pipeUnion) && pipeUnion.format === 1 && pipeUnion.sites.length > 27);
  const hits = pipeRows.filter((row) => row.case === name);
  assert.equal(hits.length, 1, 'One actual response-bound pipe entry');
  const subject = pipeUnion.sites[hits[0].index];
  assert.ok(subject && ['installed', 'generated'].includes(subject.kind));
  return Object.freeze({
    case: name,
    pipePath: subject.path,
    pipeSha256: subject.sha256,
    pipeKind: subject.kind,
    pipeBytes: subject.bytes,
    pipeEntryIndex: subject.entryIndex,
    pipeManifestSha256: subject.manifestSha256,
  });
}

export const copiedRoutePairs = EXPECTED;
