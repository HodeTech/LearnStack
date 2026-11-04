// Exact mapping positions, duplicate ordering and metadata survive semantic edits.
import assert from 'node:assert/strict';
import { SourceMap } from 'node:module';
import { editMap, startsOf, position, getOffset } from './map-editor.mjs';
import { decode, encode } from '@jridgewell/sourcemap-codec';
function equalAt(line, column, lines) {
  let found = null;
  for (const x of lines[line] || []) {
    if (x[0] > column) break;
    found = x;
  }
  return found;
}
function verify(code, map, edits) {
  const a = editMap({ code, map, edits }),
    b = editMap({ code, map, edits });
  assert.equal(a.code, b.code);
  assert.deepEqual(a.map, b.map);
  assert.deepEqual(a.map.sources.slice(0, map.sources.length), map.sources);
  assert.deepEqual(a.map.sourcesContent.slice(0, map.sources.length), map.sourcesContent);
  assert.deepEqual(a.map.names, map.names);
  for (const k of Object.keys(map))
    if (!['sources', 'sourcesContent', 'mappings'].includes(k))
      assert.deepEqual(a.map[k], map[k], k);
  assert.equal(a.map.sources.length, map.sources.length + edits.length);
  let original = decode(map.mappings),
    updated = decode(a.map.mappings),
    os = startsOf(code),
    ns = startsOf(a.code),
    expected = new Map(),
    ordered = new Map();
  const add = (k, n = 1) => expected.set(k, (expected.get(k) || 0) + n);
  for (let l = 0; l < original.length; l++)
    for (const seg of original[l]) {
      const off = getOffset(os, code, l, seg[0]);
      if (a.edits.some((e) => e.start <= off && off < e.end)) continue;
      let delta = 0;
      for (const e of a.edits) if (off >= e.end) delta += e.text.length - (e.end - e.start);
      const [nl, nc] = position(ns, off + delta);
      add(JSON.stringify([nl, nc, ...seg.slice(1)]));
      let key = nl + ':' + nc;
      if (!ordered.has(key)) ordered.set(key, []);
      ordered.get(key).push(seg.slice(1));
    }
  for (let l = 0; l < updated.length; l++)
    for (const seg of updated[l]) {
      let k = JSON.stringify([l, ...seg]);
      if (expected.get(k) > 0) expected.set(k, expected.get(k) - 1);
    }
  for (const [k, count] of expected) assert.equal(count, 0, 'lost original segment ' + k);
  const seen = new Map();
  for (let l = 0; l < updated.length; l++)
    for (const seg of updated[l]) {
      const key = l + ':' + seg[0],
        want = ordered.get(key),
        at = seen.get(key) || 0;
      if (want && at < want.length && JSON.stringify(seg.slice(1)) === JSON.stringify(want[at]))
        seen.set(key, at + 1);
    }
  for (const [key, want] of ordered)
    assert.equal(seen.get(key) || 0, want.length, 'duplicate order changed at ' + key);
  for (const e of a.edits) {
    assert.equal(a.map.sources[e.sourceIndex], e.source);
    assert.equal(a.map.sourcesContent[e.sourceIndex], e.text);
    let sl = 0,
      sc = 0;
    for (let i = 0; i < e.text.length; i++) {
      const ch = e.text.charCodeAt(i);
      if (ch === 10) {
        sl++;
        sc = 0;
        continue;
      }
      if (ch === 13 && e.text.charCodeAt(i + 1) === 10) {
        sc++;
        continue;
      }
      let [l, c] = position(ns, e.newStart + i),
        got = equalAt(l, c, updated);
      assert.deepEqual(got, [c, e.sourceIndex, sl, sc]);
      sc++;
    }
    // Right-context mapping is the old GLB from the original right edge, or unmapped.
    // If another edit immediately follows, its included source takes priority.
    if (!a.edits.some((other) => other !== e && other.newStart === e.newEnd)) {
      let [ol, oc] = position(os, e.end),
        old = equalAt(ol, oc, original),
        [nl, nc] = position(ns, e.newEnd),
        now = equalAt(nl, nc, updated);
      assert.deepEqual(now, old ? [nc, ...old.slice(1)] : [nc]);
    }
  }
  return a;
}
const m = {
  version: 3,
  file: 'bundle.js',
  sourceRoot: '/root',
  sources: ['a.ts', 'unused.ts', 'b.ts'],
  sourcesContent: ['abc\r\ndef', '', 'x'],
  names: ['unused', 'term'],
  ignoreList: [1, 2],
  mappings: encode([
    [[0, 0, 0, 0], [1, 0, 0, 1, 1], [1, 2, 0, 0], [2]],
    [
      [0, 0, 1, 0],
      [1, 0, 1, 1],
    ],
  ]),
};
const code = 'abc\r\ndef';
const sample = verify(code, m, [
  { start: 1, end: 2, text: '😀\r\nXY', source: 'learnstack-generated/edits/a.js' },
  { start: 6, end: 7, text: '', source: 'learnstack-generated/edits/b.js' },
]);
verify(code, m, [
  { start: 1, end: 1, text: '\n', source: 'learnstack-generated/edits/newline.js' },
]);
verify(code, m, [{ start: 1, end: 2, text: '', source: 'learnstack-generated/edits/delete.js' }]);
verify(code, m, [
  { start: 0, end: 1, text: 'Z', source: 'learnstack-generated/edits/one.js' },
  { start: 1, end: 2, text: 'Q', source: 'learnstack-generated/edits/two.js' },
]);
assert.deepEqual(editMap({ code, map: m, edits: [] }).map, m);
assert.throws(
  () => editMap({ code, map: { ...m, unknown: 'x' }, edits: [] }),
  /unsupported map field/,
);
assert.throws(() => editMap({ code: 'x\ry', map: m, edits: [] }), /unsupported bare CR/);
assert.throws(
  () =>
    editMap({
      code: 'a\nb',
      map: { ...m, mappings: '' },
      edits: [{ start: 1, end: 1, text: '\r', source: 'x' }],
    }),
  /unsupported bare CR/,
);
assert.throws(() => editMap({ code, map: { ...m, sections: [] }, edits: [] }), /flat source maps/);
assert.throws(
  () => editMap({ code, map: m, edits: [{ start: 4, end: 4, text: '', source: 'x' }] }),
  /splits CRLF/,
);
assert.throws(
  () =>
    editMap({
      code: '😀',
      map: { ...m, mappings: '', sourcesContent: ['abc', '', 'x'] },
      edits: [{ start: 1, end: 1, text: '', source: 'x' }],
    }),
  /surrogate/,
);
assert.throws(
  () =>
    editMap({
      code,
      map: m,
      edits: [
        { start: 0, end: 2, text: '', source: 'x' },
        { start: 1, end: 3, text: '', source: 'y' },
      ],
    }),
  /overlapping/,
);
assert.throws(
  () => editMap({ code, map: m, edits: [{ start: 0, end: 0, text: '', source: 'a.ts' }] }),
  /unique/,
);

// Node's independent consumer checks the inserted UTF-16 coordinates, rather
// than validating only against our own VLQ traversal.
const nodeSample = editMap({
  code: 'abc',
  map: {
    version: 3,
    file: 'x.js',
    sources: ['a.ts'],
    sourcesContent: ['abc'],
    names: [],
    mappings: 'AAAA',
  },
  edits: [{ start: 1, end: 2, text: '😀\nXYZ', source: 'edits/replacement.js' }],
});
const nodeConsumer = new SourceMap(nodeSample.map);
for (const [line, column, originalLine, originalColumn] of [
  [0, 1, 0, 0],
  [0, 2, 0, 1],
  [1, 2, 1, 2],
]) {
  const entry = nodeConsumer.findEntry(line, column);
  assert.equal(entry.originalSource, 'edits/replacement.js');
  assert.equal(entry.originalLine, originalLine);
  assert.equal(entry.originalColumn, originalColumn);
}
console.log('source-map controls passed');
