// Host-side finite observations, independently bound to the captured live family.
import assert from 'node:assert/strict';
import { constants, openSync, closeSync, writeSync } from 'node:fs';
import { join, resolve } from 'node:path';

import { faultCases, finalCase, recoverableCases } from './framework-router-fault-gate.mjs';
import {
  deriveFaultJoinPoints,
  deriveFaultBoundaryPoints,
} from './framework-router-fault-join-points.mjs';
import {
  createFaultFileReader,
  faultRows,
  createFaultPipeObservation,
} from './framework-router-fault-pipe-host.mjs';
import { createCopiedRouterHttpPort } from './framework-router-matrix-port-host.mjs';

const exact = (value, keys) => assert.deepEqual(Object.keys(value).sort(), [...keys].sort());
const refused = () => {
  throw new Error('Copied router fault evidence refused');
};
const inRange = (row, point) =>
  row.line >= point.line &&
  row.line <= point.endLine &&
  (row.line !== point.line || row.column >= point.column) &&
  (row.line !== point.endLine || row.column < point.endColumn);
const basic = new Set([
  'arm',
  'route',
  'pull',
  'pipe',
  'allocation',
  'source-fault',
  'encoder-fault',
  'end-bound',
  'end-reject',
  'aggregate-bound',
  'aggregate-rejected',
  'cleanup-fault',
  'constructor-fault',
  'response-finish',
  'response-close',
  'missing-refused',
  'duplicate-refused',
  'late-refused',
  'retired',
  'unconfirmed',
  'stop',
  'terminal-qualified',
  'watchdog',
]);

export function validateRouterFaultRows(
  rows,
  { pid, union, joins, boundary, routerSha256 },
  complete = false,
) {
  assert.ok(Array.isArray(rows) && rows.length <= 256);
  const cases = new Map();
  let current;
  const allowedValue = {
    transition: ['abort', 'failure'],
    'observer-joined': ['fulfilled', 'rejected'],
    'encoder-close': ['ended', 'unended'],
    handler: ['fulfilled', 'rejected'],
  };
  for (const [i, row] of rows.entries()) {
    exact(row, ['v', 'p', 'n', 'c', 'k', 'x']);
    assert.equal(row.v, 1);
    assert.equal(row.p, pid);
    assert.equal(row.n, i + 1);
    assert.ok(faultCases.includes(row.c));
    if (row.k === 'arm') {
      assert.equal(row.c, faultCases[cases.size]);
      if (current)
        assert.equal(
          current.some((value) => value.k === 'retired'),
          true,
        );
      current = [];
      cases.set(row.c, current);
    }
    assert.equal(current, cases.get(row.c));
    assert.ok(current);
    if (basic.has(row.k)) assert.equal(row.x, null);
    else if (Object.hasOwn(allowedValue, row.k)) assert.ok(allowedValue[row.k].includes(row.x));
    else if (row.k === 'join') {
      exact(row.x, ['phase', 'index', 'scriptId', 'sha256', 'line', 'column']);
      const p = row.x;
      assert.ok(['pipe', 'router'].includes(p.phase));
      assert.match(p.scriptId, /^[0-9]{1,16}$/);
      assert.match(p.sha256, /^[a-f0-9]{64}$/);
      assert.ok(
        Number.isSafeInteger(p.line) &&
          p.line >= 0 &&
          Number.isSafeInteger(p.column) &&
          p.column >= 0,
      );
      if (p.phase === 'pipe') {
        const site = union.sites[p.index];
        assert.ok(site);
        assert.equal(site.sha256, p.sha256);
        const expected = joins.find(
          (value) => value.fileIndex === site.fileIndex && value.entryIndex === site.entryIndex,
        );
        assert.ok(expected && inRange(p, expected.joinPoint));
      } else {
        assert.equal(p.index, -1);
        assert.equal(p.sha256, routerSha256);
        assert.ok(inRange(p, boundary.routerJoin));
      }
    } else if (row.k === 'result') {
      exact(row.x, ['phase', 'hasPrimary', 'hasEnd', 'hasCleanup']);
      assert.ok(['pipe', 'router'].includes(row.x.phase));
      for (const key of ['hasPrimary', 'hasEnd', 'hasCleanup'])
        assert.equal(typeof row.x[key], 'boolean');
    } else refused();
    const duplicates = current.filter(
      (value) =>
        value.k === row.k && (!['join', 'result'].includes(row.k) || value.x.phase === row.x.phase),
    );
    assert.equal(duplicates.length, 0, 'One finite fault milestone');
    current.push(row);
  }
  for (const [name, values] of cases) {
    const position = (kind) => values.findIndex((value) => value.k === kind);
    if (name !== finalCase)
      for (const kind of ['stop', 'terminal-qualified', 'watchdog'])
        assert.equal(position(kind), -1);
    const one = (kind) => {
      assert.ok(position(kind) >= 0, 'Required fault milestone ' + kind);
      return values[position(kind)];
    };
    const before = (a, b) =>
      assert.ok(position(a) >= 0 && position(b) > position(a), 'Actual fault order ' + a + ' ' + b);
    if (
      position('retired') >= 0 ||
      (name === finalCase && (position('unconfirmed') >= 0 || complete))
    ) {
      for (const kind of ['arm', 'route', 'pull', 'pipe', 'response-close']) one(kind);
      before('arm', 'route');
      before('route', 'pipe');
      const pipeJoin = values.find((row) => row.k === 'join' && row.x.phase === 'pipe');
      assert.ok(pipeJoin);
      if (name === finalCase) {
        one('constructor-fault');
        one('unconfirmed');
        before('pipe', 'constructor-fault');
        before('response-close', 'unconfirmed');
        for (const absent of [
          'allocation',
          'encoder-close',
          'observer-joined',
          'handler',
          'retired',
          'result',
          'response-finish',
        ])
          assert.equal(position(absent), -1);
        assert.equal(values.filter((row) => row.k === 'join').length, 1);
        if (complete) {
          before('unconfirmed', 'stop');
          before('stop', 'terminal-qualified');
          before('terminal-qualified', 'watchdog');
        }
        continue;
      }
      for (const kind of ['observer-joined', 'handler', 'retired']) one(kind);
      const joinRows = values.filter((row) => row.k === 'join'),
        resultRows = values.filter((row) => row.k === 'result');
      assert.deepEqual(
        joinRows.map((row) => row.x.phase),
        ['pipe', 'router'],
      );
      assert.deepEqual(
        resultRows.map((row) => row.x.phase),
        ['pipe', 'router'],
      );
      for (const phase of ['pipe', 'router'])
        assert.ok(
          values.indexOf(joinRows.find((row) => row.x.phase === phase)) <
            values.indexOf(resultRows.find((row) => row.x.phase === phase)),
        );
      before('response-close', 'retired');
      before('handler', 'retired');
      const result = resultRows[0].x;
      if (['pipe-before-selection', 'abort-before-selection'].includes(name)) {
        assert.equal(position('allocation'), -1);
        assert.equal(position('encoder-close'), -1);
        one('transition');
        assert.ok(position('transition') < values.indexOf(pipeJoin));
      } else {
        before('pipe', 'allocation');
        before('allocation', 'encoder-close');
        before('encoder-close', 'retired');
      }
      if (name === 'abort-before-selection') {
        assert.equal(one('transition').x, 'abort');
        assert.equal(position('response-finish'), -1);
        assert.equal(result.hasCleanup, false);
      }
      if (
        ['pipe-before-selection', 'source-fault-gzip', 'source-cleanup-fault-gzip'].includes(name)
      ) {
        before('pipe', 'source-fault');
        before('source-fault', 'transition');
        assert.equal(result.hasPrimary, true);
      }
      if (name === 'encoder-early-fault-gzip') {
        before('allocation', 'encoder-fault');
        before('encoder-fault', 'encoder-close');
        assert.equal(result.hasCleanup, true);
      }
      if (name === 'source-cleanup-fault-gzip') {
        before('source-fault', 'cleanup-fault');
        assert.equal(result.hasCleanup, true);
      }
      if (name === 'end-work-reject-gzip') {
        for (const kind of ['end-bound', 'aggregate-bound', 'end-reject', 'aggregate-rejected'])
          one(kind);
        before('end-bound', 'aggregate-bound');
        before('aggregate-bound', 'pipe');
        before('allocation', 'end-reject');
        before('end-reject', 'aggregate-rejected');
        assert.ok(position('aggregate-rejected') < values.indexOf(pipeJoin));
        assert.equal(result.hasEnd, true);
      }
      if (name === 'finish-cleanup-fault-gzip') {
        before('response-finish', 'cleanup-fault');
        before('cleanup-fault', 'retired');
        for (const kind of ['missing-refused', 'duplicate-refused', 'late-refused']) one(kind);
        before('missing-refused', 'allocation');
        before('duplicate-refused', 'allocation');
        before('encoder-close', 'late-refused');
        assert.equal(result.hasPrimary, false);
        assert.equal(result.hasCleanup, true);
      } else
        for (const kind of ['missing-refused', 'duplicate-refused', 'late-refused'])
          assert.equal(position(kind), -1);
    }
  }
  if (complete) assert.deepEqual([...cases.keys()], faultCases);
  return cases;
}

export function createRouterFaultHost({ app, broker, identity, union, subjects }) {
  try {
    const root = resolve(app, '../../..');
    const port = createCopiedRouterHttpPort({
      app,
      broker,
      union,
      compressionSha256: subjects.compression.sha256,
    });
    const pipes = createFaultPipeObservation({
      app,
      broker,
      union,
      compressionSha256: subjects.compression.sha256,
    });
    const family = broker.capture.family(),
      lease = broker.capture.lease();
    const joins = deriveFaultJoinPoints(identity, union.files, subjects.parser),
      boundary = deriveFaultBoundaryPoints(identity, subjects);
    const context = {
      pid: family.pid,
      union,
      joins,
      boundary,
      routerSha256: subjects.router.sha256,
    };
    const read = createFaultFileReader(join(root, '.router-fault.events'), 256 * 768);
    let armed = 0;
    function inspect(complete = false) {
      try {
        port.assertBinding();
        assert.equal(broker.capture.family(), family);
        assert.equal(broker.capture.lease(), lease);
        const rows = faultRows(read(), 256, 768);
        const cases = validateRouterFaultRows(rows, context, complete);
        const pipe = pipes.inspect(complete);
        for (const value of rows.filter((row) => row.k === 'join' && row.x.phase === 'pipe')) {
          const entry = pipe.attestations.find((row) => row.case === value.c);
          assert.ok(entry);
          assert.equal(value.x.index, entry.index);
          assert.equal(value.x.scriptId, entry.scriptId);
          assert.equal(value.x.sha256, entry.sha256);
        }
        return Object.freeze({ rows, cases, pipe });
      } catch {
        refused();
      }
    }
    return Object.freeze({
      port: port.port,
      family,
      arm(name) {
        try {
          port.assertBinding();
          assert.equal(name, faultCases[armed]);
          const previous = inspect();
          if (armed)
            assert.ok(
              previous.cases.get(faultCases[armed - 1])?.some((row) => row.k === 'retired'),
            );
          const fd = openSync(
            join(root, '.transport-proof.arm'),
            constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | constants.O_NOFOLLOW,
            0o600,
          );
          try {
            assert.equal(writeSync(fd, name + '\n'), Buffer.byteLength(name + '\n'));
          } finally {
            closeSync(fd);
          }
          armed++;
        } catch {
          refused();
        }
      },
      inspect,
      completed(name) {
        const found = inspect().cases.get(name);
        return !!found?.some((row) => row.k === (name === finalCase ? 'unconfirmed' : 'retired'));
      },
      case(name) {
        const found = inspect();
        const rows = found.cases.get(name);
        assert.ok(rows);
        assert.ok(rows.some((row) => row.k === (name === finalCase ? 'unconfirmed' : 'retired')));
        pipes.assertCase(name);
        return Object.freeze({ case: name, events: rows });
      },
      closed() {
        assert.equal(broker.capture.retired(), true);
        assert.equal(armed, 8);
        return inspect(true);
      },
      recoverableCases,
    });
  } catch {
    refused();
  }
}
