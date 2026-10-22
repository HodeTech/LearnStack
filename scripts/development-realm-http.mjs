import http from 'node:http';
import https from 'node:https';
import { RealmError, refuse } from './development-realm-errors.mjs';

export const realmHttpMilliseconds = 5000;
export const realmBodyBytes = 256 * 1024;
const serverInformationBodyBytes = 512 * 1024;

/** Each exchange owns its request, response and agent until actual close. */
export function realmRequest(url, { method = 'GET', json, form, bearer, signal, ca, stage = 'plan' } = {}) {
  if (!(url instanceof URL) || !['http:', 'https:'].includes(url.protocol)
      || url.username || url.password || url.hash || (json !== undefined && form !== undefined))
    refuse(stage, 'configuration');
  if (signal?.aborted) refuse(stage, 'cancelled');
  const body = json !== undefined ? Buffer.from(JSON.stringify(json))
    : form !== undefined ? Buffer.from(new URLSearchParams(form).toString()) : undefined;
  if (body && body.length > realmBodyBytes) refuse(stage, 'body_limit');
  const secure = url.protocol === 'https:';
  const agent = secure ? new https.Agent({ keepAlive: false, ca, rejectUnauthorized: true })
    : new http.Agent({ keepAlive: false });
  return new Promise((resolve, reject) => {
    let request;
    let response;
    let failure;
    let ended = false;
    let requestClosed = false;
    let responseClosed = false;
    let size = 0;
    const chunks = [];
    let timer;
    const finish = () => {
      if (!requestClosed || (response && !responseClosed)) return;
      clearTimeout(timer);
      signal?.removeEventListener('abort', cancelled);
      agent.destroy();
      if (failure) reject(failure);
      else if (!ended) reject(new RealmError(stage, 'transport'));
      else if (size > 0 && !/^application\/(?:json|[a-z0-9.+-]+\+json)(?:\s*;|$)/i.test(response.headers['content-type'] ?? ''))
        reject(new RealmError(stage, 'response'));
      else resolve({ status: response.statusCode, bytes: Buffer.concat(chunks) });
    };
    const fail = (reason) => {
      failure ??= new RealmError(stage, reason);
      response?.destroy();
      request?.destroy();
    };
    const cancelled = () => fail('cancelled');
    try {
      request = (secure ? https : http).request(url, {
        method, agent,
        // Retain the issuer/Host and normal TLS hostname checks without allowing
        // a workstation DNS/hosts override to send private setup credentials away.
        ...(url.hostname === 'localhost' ? { family: 4, lookup: (_host, options, callback) => {
          if (options.all) callback(null, [{ address: '127.0.0.1', family: 4 }]);
          else callback(null, '127.0.0.1', 4);
        } } : {}),
        headers: {
          Accept: 'application/json', 'Accept-Encoding': 'identity',
          ...(body ? { 'Content-Type': form ? 'application/x-www-form-urlencoded' : 'application/json',
            'Content-Length': String(body.length) } : {}),
          ...(bearer ? { Authorization: `Bearer ${bearer}` } : {}),
        },
      });
      request.once('response', (value) => {
        response = value;
        value.once('close', () => { responseClosed = true; finish(); });
        value.once('error', () => fail('transport'));
        value.once('aborted', () => fail('transport'));
        if (value.statusCode >= 300 && value.statusCode < 400) return fail('redirect');
        if (value.headers['content-encoding'] && value.headers['content-encoding'] !== 'identity')
          return fail('response');
        // The accepted exception belongs to this exact authenticated successful
        // read, never a caller-supplied limit or a general Admin REST allowance.
        const responseBytes = method === 'GET' && url.pathname === '/admin/serverinfo'
          && !url.search && json === undefined && form === undefined
          && typeof bearer === 'string' && bearer.length > 0 && !/\s/.test(bearer)
          && value.statusCode === 200 ? serverInformationBodyBytes : realmBodyBytes;
        value.on('data', (chunk) => {
          size += chunk.length;
          if (size > responseBytes) fail('body_limit');
          else chunks.push(chunk);
        });
        value.once('end', () => { ended = true; });
      });
      request.once('error', () => fail('transport'));
      request.once('close', () => { requestClosed = true; finish(); });
      timer = setTimeout(() => fail('deadline'), realmHttpMilliseconds);
      signal?.addEventListener('abort', cancelled, { once: true });
      if (signal?.aborted) cancelled();
      request.end(body);
    } catch {
      failure = new RealmError(stage, 'transport');
      if (request) request.destroy();
      else {
        requestClosed = true;
        finish();
      }
    }
  });
}

export function realmJson(result, stage) {
  try { return JSON.parse(result.bytes.toString('utf8')); }
  catch { refuse(stage, 'response'); }
}
