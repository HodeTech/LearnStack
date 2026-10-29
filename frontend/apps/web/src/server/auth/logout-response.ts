import type { ServerResponse } from 'node:http';

/** Next serializes a middleware redirect's Location as a body. Logout hints
 * are allowed only in the fixed provider Location, never that framework body. */
export function protectBffLogoutResponse(response: ServerResponse, issuer: string, host: string) {
  const writeHead = response.writeHead;
  const write = response.write;
  const end = response.end;
  let protectedRedirect = false;

  const prepare = () => {
    if (response.statusCode !== 303) return;
    protectedRedirect = true;
    const location = response.getHeader('location');
    let allowed =
      location === '/' ||
      location === 'https://' + host + '/' ||
      location === '/api/auth/signin' ||
      location === 'https://' + host + '/api/auth/signin';
    if (typeof location === 'string' && !allowed) {
      try {
        const url = new URL(location);
        allowed =
          url.origin + url.pathname === issuer + '/protocol/openid-connect/logout' &&
          !url.hash &&
          !url.username &&
          !url.password;
      } catch {
        allowed = false;
      }
    }
    if (!allowed) {
      response.statusCode = 503;
      response.removeHeader('location');
      response.setHeader('retry-after', '30');
    }
    response.setHeader('cache-control', 'no-store');
    response.setHeader('referrer-policy', 'no-referrer');
    response.removeHeader('transfer-encoding');
    response.setHeader('content-length', '0');
  };

  response.writeHead = function (this: ServerResponse, status, message, headers) {
    this.statusCode = status;
    const supplied = typeof message === 'string' ? headers : (message ?? headers);
    if (supplied) {
      if (Array.isArray(supplied)) {
        const values = new Map<string, string[]>();
        for (let index = 0; index < supplied.length; index += 2) {
          const name = supplied[index];
          if (typeof name !== 'string') throw new TypeError('Invalid response header name');
          const key = name.toLowerCase();
          const entries = values.get(key) ?? [];
          entries.push(String(supplied[index + 1]));
          values.set(key, entries);
        }
        for (const [name, entries] of values)
          this.setHeader(name, entries.length === 1 ? entries[0]! : entries);
      } else {
        for (const [name, value] of Object.entries(supplied))
          if (value !== undefined) this.setHeader(name, value);
      }
    }
    prepare();
    return Reflect.apply(
      writeHead,
      this,
      typeof message === 'string' ? [this.statusCode, message] : [this.statusCode],
    );
  } as ServerResponse['writeHead'];

  response.write = function (this: ServerResponse, ...args: Parameters<ServerResponse['write']>) {
    if (!this.headersSent) prepare();
    if (!protectedRedirect) return write.apply(this, args);
    const callback = args.find((value) => typeof value === 'function');
    if (typeof callback === 'function') queueMicrotask(() => callback());
    return true;
  } as ServerResponse['write'];

  response.end = function (this: ServerResponse, ...args: Parameters<ServerResponse['end']>) {
    if (!this.headersSent) prepare();
    if (!protectedRedirect) return end.apply(this, args);
    const callback = args.find((value) => typeof value === 'function');
    return Reflect.apply(end, this, typeof callback === 'function' ? [callback] : []);
  } as ServerResponse['end'];
}
