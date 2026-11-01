// Lossless flat source-map composition for ADR-0083's parsed edits.
import { decode, encode } from '@jridgewell/sourcemap-codec';
const known = new Set([
  'version',
  'file',
  'sourceRoot',
  'sources',
  'sourcesContent',
  'names',
  'mappings',
  'ignoreList',
]);
function invariant(ok, message) {
  if (!ok) throw Error(message);
}
function supportedLines(text) {
  invariant(
    !/\r(?!\n)|[\u2028\u2029]/u.test(text),
    'unsupported bare CR or Unicode line separator',
  );
}
function startsOf(text) {
  const a = [0];
  for (let i = 0; i < text.length; i++) if (text.charCodeAt(i) === 10) a.push(i + 1);
  return a;
}
function position(starts, offset) {
  let lo = 0,
    hi = starts.length;
  while (lo + 1 < hi) {
    let mid = (lo + hi) >>> 1;
    if (starts[mid] <= offset) lo = mid;
    else hi = mid;
  }
  return [lo, offset - starts[lo]];
}
function getOffset(starts, text, line, col) {
  invariant(line < starts.length && line >= 0, 'generated line out of range');
  const end = line + 1 < starts.length ? starts[line + 1] - 1 : text.length;
  invariant(
    Number.isInteger(col) && col >= 0 && starts[line] + col <= end,
    'generated column out of range',
  );
  return starts[line] + col;
}
function boundary(text, p) {
  invariant(Number.isInteger(p) && p >= 0 && p <= text.length, 'invalid edit offset');
  invariant(
    !(p > 0 && text.charCodeAt(p - 1) === 13 && text.charCodeAt(p) === 10),
    'edit splits CRLF',
  );
  invariant(
    !(
      p > 0 &&
      p < text.length &&
      text.charCodeAt(p - 1) >= 0xd800 &&
      text.charCodeAt(p - 1) <= 0xdbff &&
      text.charCodeAt(p) >= 0xdc00 &&
      text.charCodeAt(p) <= 0xdfff
    ),
    'edit splits UTF-16 surrogate pair',
  );
}
function editMap({ code, map, edits }) {
  invariant(typeof code === 'string' && map && Array.isArray(edits), 'invalid arguments');
  invariant(
    map.version === 3 && !('sections' in map),
    'only flat source maps version 3 are supported',
  );
  for (const k of Object.keys(map)) invariant(known.has(k), 'unsupported map field: ' + k);
  if ('file' in map) invariant(typeof map.file === 'string', 'invalid file');
  if ('sourceRoot' in map) invariant(typeof map.sourceRoot === 'string', 'invalid sourceRoot');
  invariant(
    Array.isArray(map.sources) && map.sources.every((s) => typeof s === 'string'),
    'invalid sources',
  );
  invariant(
    Array.isArray(map.sourcesContent) &&
      map.sourcesContent.length === map.sources.length &&
      map.sourcesContent.every((s) => s === null || typeof s === 'string'),
    'invalid sourcesContent',
  );
  invariant(
    Array.isArray(map.names) &&
      map.names.every((s) => typeof s === 'string') &&
      typeof map.mappings === 'string',
    'invalid map arrays',
  );
  if ('ignoreList' in map)
    invariant(
      Array.isArray(map.ignoreList) &&
        map.ignoreList.every((i) => Number.isInteger(i) && i >= 0 && i < map.sources.length),
      'invalid ignoreList',
    );
  const decoded = decode(map.mappings);
  invariant(encode(decoded) === map.mappings, 'noncanonical or invalid VLQ mappings');
  supportedLines(code);
  const oldStarts = startsOf(code),
    sorted = edits.map((e) => ({ ...e })).sort((a, b) => a.start - b.start || a.end - b.end);
  const sources = [...map.sources],
    contents = [...map.sourcesContent];
  let previous = null;
  for (const e of sorted) {
    boundary(code, e.start);
    boundary(code, e.end);
    invariant(
      e.start <= e.end && (!previous || (e.start >= previous.end && e.start > previous.start)),
      'overlapping or ambiguous edits',
    );
    previous = e;
    invariant(
      typeof e.text === 'string' && typeof e.source === 'string' && e.source.length > 0,
      'invalid edit source',
    );
    supportedLines(e.text);
    invariant(!sources.includes(e.source), 'edit source must be unique');
    invariant(
      !e.source.startsWith('/') && !e.source.includes('://') && !e.source.split('/').includes('..'),
      'edit source must be a relative path',
    );
    e.sourceIndex = sources.length;
    sources.push(e.source);
    contents.push(e.text);
  }
  let out = '',
    cursor = 0;
  for (const e of sorted) {
    out += code.slice(cursor, e.start);
    e.newStart = out.length;
    out += e.text;
    e.newEnd = out.length;
    cursor = e.end;
  }
  out += code.slice(cursor);
  supportedLines(out);
  for (const e of sorted) {
    boundary(out, e.newStart);
    boundary(out, e.newEnd);
  }
  const newStarts = startsOf(out),
    shifted = [];
  let editIdx = 0,
    delta = 0,
    removed = 0,
    kept = 0;
  // Tuples preserve their original source/name indices and order at duplicate positions.
  for (let line = 0; line < decoded.length; line++) {
    let lastColumn = -1;
    for (const tuple of decoded[line]) {
      invariant(tuple[0] >= lastColumn, 'unsorted generated tuples');
      lastColumn = tuple[0];
      invariant(
        [1, 4, 5].includes(tuple.length) && tuple.every(Number.isInteger),
        'unsupported tuple',
      );
      invariant(
        tuple.length === 1 ||
          (tuple[1] >= 0 && tuple[1] < map.sources.length && tuple[2] >= 0 && tuple[3] >= 0),
        'invalid original tuple',
      );
      invariant(
        tuple.length < 5 || (tuple[4] >= 0 && tuple[4] < map.names.length),
        'invalid name index',
      );
      const off = getOffset(oldStarts, code, line, tuple[0]);
      while (editIdx < sorted.length && off >= sorted[editIdx].end) {
        delta += sorted[editIdx].text.length - (sorted[editIdx].end - sorted[editIdx].start);
        editIdx++;
      }
      if (editIdx < sorted.length && off >= sorted[editIdx].start && off < sorted[editIdx].end) {
        removed++;
        continue;
      }
      const [l, c] = position(newStarts, off + delta);
      shifted.push({ line: l, tuple: [c, ...tuple.slice(1)], order: 2 });
      kept++;
    }
  }
  // At each edit's right edge restore the mapping that applied there in the original,
  // unless one or more original tuples start there and have already been moved.
  // All coordinates and source indices are raw and zero-based.
  for (const e of sorted) {
    const [ol, oc] = position(oldStarts, e.end),
      line = decoded[ol] || [];
    let prior = null,
      exact = false;
    for (const t of line) {
      if (t[0] > oc) break;
      if (t[0] === oc) exact = true;
      prior = t;
    }
    if (!exact && !sorted.some((other) => other !== e && other.newStart === e.newEnd)) {
      const [l, c] = position(newStarts, e.newEnd);
      shifted.push({ line: l, tuple: prior ? [c, ...prior.slice(1)] : [c], order: 3 });
    }
    if (e.text.length !== 0) {
      // Map every non-newline UTF-16 code unit to the identical included edit source.
      // A leading line break has no mapped code unit, so explicitly stop inheritance.
      // Otherwise the first character's exact included-source mapping itself is the boundary.
      if (e.text.charCodeAt(0) === 10 || e.text.charCodeAt(0) === 13) {
        const [l, c] = position(newStarts, e.newStart);
        shifted.push({ line: l, tuple: [c], order: 1 });
      }
      let sourceLine = 0,
        sourceCol = 0;
      for (let i = 0; i < e.text.length; i++) {
        const ch = e.text.charCodeAt(i);
        if (ch === 10) {
          sourceLine++;
          sourceCol = 0;
          continue;
        }
        if (ch === 13 && e.text.charCodeAt(i + 1) === 10) {
          sourceCol++;
          continue;
        }
        const [gl, gc] = position(newStarts, e.newStart + i);
        shifted.push({ line: gl, tuple: [gc, e.sourceIndex, sourceLine, sourceCol], order: 4 });
        sourceCol++;
      }
    }
  }
  shifted.sort((a, b) => a.line - b.line || a.tuple[0] - b.tuple[0] || a.order - b.order);
  const resultLines = Array.from({ length: newStarts.length }, () => []);
  for (const s of shifted) resultLines[s.line].push(s.tuple);
  // Keep the original flat-map keys and index-bearing metadata byte-for-byte as JS values.
  const outMap = {
    ...map,
    sources,
    sourcesContent: contents,
    mappings: sorted.length ? encode(resultLines) : map.mappings,
  };
  return {
    code: out,
    map: outMap,
    stats: { originalSegments: kept + removed, kept, removed, insertedSources: sorted.length },
    edits: sorted,
  };
}
export { editMap, startsOf, position, getOffset };
