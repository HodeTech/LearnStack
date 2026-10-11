import { StringDecoder } from 'node:string_decoder';

/** Consume structured completion events; retain counts, never raw logs or scope data. */
export function apiCompletionObserver() {
  const state = { total: 0, statuses: {}, paths: {}, malformed: false, pendingBytes: 0 };
  const streams = new Map();
  function consume(chunk, stream = 'stdout') {
    let input = streams.get(stream);
    if (!input) {
      input = { decoder: new StringDecoder('utf8'), tail: '' };
      streams.set(stream, input);
    }
    input.tail += input.decoder.write(chunk);
    if (input.tail.length > 1024 * 1024) throw new Error('Unbounded API completion line');
    const lines = input.tail.split('\n');
    input.tail = lines.pop();
    state.pendingBytes = [...streams.values()].reduce(
      (count, value) => count + Buffer.byteLength(value.tail),
      0,
    );
    for (const line of lines) {
      let value;
      try {
        value = JSON.parse(line);
      } catch {
        // The startup banner is plaintext; a damaged structured record is not.
        if (line.trimStart().startsWith('{')) state.malformed = true;
        continue;
      }
      if (!value || typeof value !== 'object') continue;
      if (
        value.SourceContext !== 'Microsoft.AspNetCore.Hosting.Diagnostics' ||
        value.EventId?.Id !== 2
      )
        continue;
      if (typeof value.Path !== 'string' || !Number.isInteger(value.StatusCode)) {
        state.malformed = true;
        continue;
      }
      if (!value.Path.startsWith('/api/v1/public/')) continue;
      if (
        !['GET', 'HEAD'].includes(value.Method) ||
        !/^\/api\/v1\/public\/(?:site|courses(?:\/[^/]+(?:\/lessons(?:\/[^/]+)?)?)?)$/.test(
          value.Path,
        )
      ) {
        state.malformed = true;
        continue;
      }
      state.total++;
      state.statuses[value.StatusCode] = (state.statuses[value.StatusCode] ?? 0) + 1;
      // Resource slugs are public, but receipts need only the endpoint family.
      const family =
        value.Path === '/api/v1/public/site'
          ? 'site'
          : value.Path.includes('/lessons/')
            ? 'lesson'
            : 'course';
      state.paths[family] = (state.paths[family] ?? 0) + 1;
    }
  }
  function finish() {
    for (const input of streams.values()) {
      if ((input.tail + input.decoder.end()).trim()) state.malformed = true;
      input.tail = '';
    }
    state.pendingBytes = 0;
  }
  return { state, consume, finish };
}

export function completionDelta(before, after, expected) {
  if (
    after.malformed ||
    after.pendingBytes !== 0 ||
    after.statuses[429] ||
    after.total - before.total !== expected ||
    expected > 48
  )
    throw new Error('Public API completion census refused');
  const statuses = Object.fromEntries(
    Object.entries(after.statuses).map(([code, count]) => [
      code,
      count - (before.statuses[code] ?? 0),
    ]),
  );
  if (Object.entries(statuses).some(([code, count]) => count && code !== '200'))
    throw new Error('Public API completion status refused');
  return { count: expected, statuses };
}

/** Only joined API output can establish the final, unchanged successful census. */
export function finalCompletionCensus(beforeCleanup, afterCleanup, expected) {
  completionDelta(beforeCleanup, afterCleanup, 0);
  if (
    afterCleanup.total !== expected ||
    Object.entries(afterCleanup.statuses).some(([code, count]) => count && code !== '200')
  )
    throw new Error('Final API completion census refused');
}
