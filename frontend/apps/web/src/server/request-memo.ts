import 'server-only';

import { headers } from 'next/headers';

/**
 * Next's request headers retain their identity when an error render replaces
 * React's cache. Weak keys keep reuse inside that incoming request, including
 * rejected work, without retaining requests after their framework lifetime.
 * The public source guard constrains this small mechanism; the production
 * fixture proves the pinned Next runtime's identity and freshness behavior.
 */
export function requestMemo<T>(
  load: (incoming: Awaited<ReturnType<typeof headers>>) => Promise<T>,
): () => Promise<T> {
  const requests = new WeakMap<Awaited<ReturnType<typeof headers>>, Promise<T>>();
  return async () => {
    const incoming = await headers();
    let pending = requests.get(incoming);
    if (!pending) {
      pending = Promise.resolve().then(() => load(incoming));
      requests.set(incoming, pending);
    }
    return pending;
  };
}
