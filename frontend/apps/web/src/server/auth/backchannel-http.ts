import type { IncomingMessage, ServerResponse } from 'node:http';

import { canonicalAddress } from '../ingress.js';
import type { VerifiedLogoutToken } from './backchannel-contract.js';

export const BACKCHANNEL_PATH = '/api/auth/backchannel/keycloak';
export type BackchannelDiagnostic = 'accepted' | 'duplicate' | 'refused' | 'unavailable';
type Stage = 'provider' | 'native' | 'clock' | 'capacity' | 'stopped';
export type BackchannelDiagnosticSink = (category: BackchannelDiagnostic, stage?: Stage) => void;
type Outcome = BackchannelDiagnostic | { category: BackchannelDiagnostic; stage: Stage };
export function createBackchannelAdmission() {
  let active = 0;
  const peers = new Map<string, number>();
  return {
    reserve(peer: string) {
      const key = canonicalAddress(peer);
      if (!key || active >= 8 || (peers.get(key) ?? 0) >= 2) return null;
      active++;
      peers.set(key, (peers.get(key) ?? 0) + 1);
      let released = false;
      return () => {
        if (released) return;
        released = true;
        active--;
        const count = peers.get(key)! - 1;
        if (count) peers.set(key, count);
        else peers.delete(key);
      };
    },
    counts: () => Object.freeze({ active, peers: peers.size }),
  };
}
export type Admission = ReturnType<typeof createBackchannelAdmission>;
function headers(request: IncomingMessage, authority: string) {
  const values = (name: string) =>
    request.rawHeaders.flatMap((value, index) =>
      index % 2 === 0 && value.toLowerCase() === name ? [request.rawHeaders[index + 1]] : [],
    );
  const host = values('host'),
    type = values('content-type'),
    length = values('content-length');
  return (
    request.method === 'POST' &&
    request.url === BACKCHANNEL_PATH &&
    host.length === 1 &&
    host[0] === authority &&
    type.length === 1 &&
    /^application\/x-www-form-urlencoded(?:\s*;\s*charset\s*=\s*(?:utf-8|"utf-8"))?\s*$/iu.test(
      type[0] ?? '',
    ) &&
    length.length <= 1 &&
    (!length.length || (/^(?:0|[1-9]\d*)$/u.test(length[0]!) && Number(length[0]) <= 16384)) &&
    !(length.length && values('transfer-encoding').length)
  );
}
function parse(bytes: Buffer) {
  const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  let token: string | undefined;
  for (const item of text.split('&')) {
    const at = item.indexOf('=');
    const decode = (value: string) => decodeURIComponent(value.replace(/\+/gu, ' '));
    const key = decode(at < 0 ? item : item.slice(0, at));
    const value = decode(at < 0 ? '' : item.slice(at + 1));
    if (key === 'logout_token') {
      if (token !== undefined) throw new Error();
      token = value;
    }
  }
  if (!token) throw new Error();
  return token;
}
function end(response: ServerResponse, status: number) {
  if (!response.headersSent && !response.destroyed) {
    response.writeHead(status, { 'cache-control': 'no-store', 'content-length': '0' });
    response.end();
  }
}
function refuseRequest(request: IncomingMessage, response: ServerResponse) {
  response.shouldKeepAlive = false;
  end(response, 400);
  if (response.writableFinished || response.destroyed) request.destroy();
  else {
    response.once('finish', () => request.destroy());
    response.once('close', () => request.destroy());
  }
}
export function createBackchannelHttp(options: {
  authority: string;
  admission: Admission;
  process: (token: string, bytes: Buffer) => Promise<Outcome>;
  diagnostic?: BackchannelDiagnosticSink;
}) {
  let stopping = false;
  const tasks = new Set<Promise<void>>();
  const requests = new Set<IncomingMessage>();
  function handle(request: IncomingMessage, response: ServerResponse) {
    if (stopping || !headers(request, options.authority)) {
      options.diagnostic?.(stopping ? 'unavailable' : 'refused', stopping ? 'stopped' : undefined);
      refuseRequest(request, response);
      return;
    }
    const release = options.admission.reserve(request.socket.remoteAddress ?? '');
    if (!release) {
      options.diagnostic?.('unavailable', 'capacity');
      refuseRequest(request, response);
      return;
    }
    requests.add(request);
    const actual = (async () => {
      let outcome: Outcome = 'refused';
      try {
        const chunks: Buffer[] = [];
        let size = 0;
        for await (const chunk of request) {
          const bytes = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
          size += bytes.length;
          if (size > 16384) {
            request.pause();
            refuseRequest(request, response);
          } else chunks.push(bytes);
        }
        if (size > 16384) throw new Error();
        const body = Buffer.concat(chunks);
        const token = parse(body);
        outcome = await options.process(token, body);
      } catch {
        outcome = 'refused';
      }
      const result = typeof outcome === 'string' ? outcome : outcome.category;
      options.diagnostic?.(result, typeof outcome === 'string' ? undefined : outcome.stage);
      end(response, result === 'accepted' || result === 'duplicate' ? 200 : 400);
      if (!response.writableFinished && !response.destroyed)
        await new Promise<void>((resolve) => {
          response.once('finish', resolve);
          response.once('close', resolve);
        });
      if (!request.closed) await new Promise<void>((resolve) => request.once('close', resolve));
    })();
    tasks.add(actual);
    void actual.finally(() => {
      requests.delete(request);
      release();
      tasks.delete(actual);
    });
  }
  return {
    handle,
    async stop() {
      stopping = true;
      for (const request of requests) request.destroy();
      while (tasks.size) await Promise.allSettled([...tasks]);
    },
  };
}
export function createNativeBackchannel(options: {
  authority: string;
  admission: Admission;
  verify: (token: string) => Promise<VerifiedLogoutToken>;
  apply: (token: VerifiedLogoutToken) => 'accepted' | 'duplicate';
  diagnostic?: BackchannelDiagnosticSink;
}) {
  return createBackchannelHttp({
    ...options,
    async process(token) {
      let verified: VerifiedLogoutToken;
      try {
        verified = await options.verify(token);
      } catch (error) {
        const stage = (error as { stage?: string })?.stage;
        if (stage === 'provider' || stage === 'clock' || stage === 'capacity')
          return { category: 'unavailable', stage };
        const reason = (error as { reason?: string })?.reason;
        return reason &&
          ['transport', 'deadline', 'provider_refused', 'configuration', 'cancelled'].includes(
            reason,
          )
          ? { category: 'unavailable', stage: 'provider' }
          : 'refused';
      }
      try {
        return options.apply(verified);
      } catch (error) {
        const reason = (error as { reason?: string })?.reason;
        return reason === 'capacity' || reason === 'clock' || reason === 'stopped'
          ? { category: 'unavailable', stage: reason }
          : 'refused';
      }
    },
  });
}
