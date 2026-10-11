import { StringDecoder } from 'node:string_decoder';

/** Consume structured completion events; retain counts, never raw logs or scope data. */
export function apiCompletionObserver() {
  const state = { total: 0, statuses: {}, paths: {}, malformed: false };
  const decoder = new StringDecoder('utf8');
  let tail = '';
  function consume(chunk) {
    tail += decoder.write(chunk);
    if (tail.length > 1024 * 1024) throw new Error('Unbounded API completion line');
    const lines = tail.split('\n');
    tail = lines.pop();
    for (const line of lines) {
      let value;
      try {
        value = JSON.parse(line);
      } catch {
        continue;
      }
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
  return { state, consume };
}

export function completionDelta(before, after, expected) {
  if (
    after.malformed ||
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
