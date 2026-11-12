import { X509Certificate } from 'node:crypto';
import { closeSync, constants, fstatSync, openSync, readSync } from 'node:fs';
import { createServer, request as httpsRequest } from 'node:https';

import type { BackchannelConfiguration } from './backchannel-configuration.js';
import {
  createBackchannelHttp,
  type Admission,
  type BackchannelDiagnosticSink,
} from './backchannel-http.js';

function ownedFile(path: string, privateKey = false): Buffer {
  const fd = openSync(path, constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK);
  try {
    const stat = fstatSync(fd);
    if (
      !stat.isFile() ||
      stat.uid !== process.getuid?.() ||
      stat.nlink !== 1 ||
      stat.size < 1 ||
      stat.size > 256 * 1024 ||
      stat.mode & (privateKey ? 0o077 : 0o022)
    )
      throw new Error('Backchannel file refused');
    const bytes = Buffer.alloc(stat.size + 1);
    let size = 0;
    while (size < bytes.length) {
      const count = readSync(fd, bytes, size, bytes.length - size, null);
      if (!count) break;
      size += count;
    }
    if (size !== stat.size) throw new Error('Backchannel file refused');
    return bytes.subarray(0, size);
  } finally {
    closeSync(fd);
  }
}
type Enabled = Extract<BackchannelConfiguration, { enabled: true }>;
export function createBackchannelBridge(
  configuration: Enabled,
  admission: Admission,
  nativePort: number,
  diagnostic?: BackchannelDiagnosticSink,
) {
  const certificate = ownedFile(configuration.certificate);
  const ca = new X509Certificate(ownedFile(configuration.ca));
  const leaf = new X509Certificate(certificate);
  const hostname = new URL(`https://${configuration.authority}`).hostname;
  const now = Date.now();
  if (
    !ca.ca ||
    !ca.checkIssued(ca) ||
    !ca.verify(ca.publicKey) ||
    now < Date.parse(ca.validFrom) ||
    now >= Date.parse(ca.validTo) ||
    now < Date.parse(leaf.validFrom) ||
    now >= Date.parse(leaf.validTo) ||
    !(leaf.checkHost(hostname) || leaf.checkIP(hostname)) ||
    !leaf.checkIssued(ca) ||
    !leaf.verify(ca.publicKey)
  )
    throw new Error('Backchannel certificate refused');
  const nativeCa = ownedFile(configuration.nativeCa);
  const outgoing = new Set<ReturnType<typeof httpsRequest>>();
  const handler = createBackchannelHttp({
    authority: configuration.authority,
    admission,
    diagnostic,
    async process(_token, body) {
      return new Promise<'accepted' | 'refused' | { category: 'unavailable'; stage: 'native' }>(
        (resolve) => {
          let result: 'accepted' | 'refused' | { category: 'unavailable'; stage: 'native' } = {
            category: 'unavailable',
            stage: 'native',
          };
          const request = httpsRequest(
            {
              hostname: '127.0.0.1',
              port: nativePort,
              servername: 'localhost',
              ca: nativeCa,
              rejectUnauthorized: true,
              method: 'POST',
              path: '/api/auth/backchannel/keycloak',
              headers: {
                host: configuration.authority,
                'content-type': 'application/x-www-form-urlencoded',
                'content-length': body.length,
              },
              agent: false,
            },
            (response) => {
              response.resume();
              response.once('end', () => {
                result = response.statusCode === 200 ? 'accepted' : 'refused';
              });
              response.once('error', () => {
                result = { category: 'unavailable', stage: 'native' };
              });
            },
          );
          outgoing.add(request);
          request.once('close', () => {
            outgoing.delete(request);
            resolve(result);
          });
          request.once('error', () => {
            result = { category: 'unavailable', stage: 'native' };
          });
          request.end(body);
        },
      );
    },
  });
  const server = createServer(
    { cert: certificate, key: ownedFile(configuration.privateKey, true), minVersion: 'TLSv1.2' },
    handler.handle,
  );
  server.on('upgrade', (_req, socket) => socket.destroy());
  server.on('connect', (_req, socket) => socket.destroy());
  let stopping = false;
  let startTask: Promise<void> | undefined;
  let stopTask: Promise<void> | undefined;
  return {
    server,
    start() {
      if (stopping || startTask) throw new Error('Backchannel listener unavailable');
      startTask = new Promise<void>((resolve, reject) => {
        const failed = () => reject(new Error('Backchannel listener failed'));
        server.once('error', failed);
        try {
          server.listen(configuration.port, configuration.bindAddress, () => {
            server.removeListener('error', failed);
            resolve();
          });
        } catch {
          server.removeListener('error', failed);
          reject(new Error('Backchannel listener failed'));
        }
      });
      return startTask;
    },
    stop() {
      if (stopTask) return stopTask;
      stopping = true;
      const handlerStopped = handler.stop();
      for (const request of outgoing) request.destroy();
      stopTask = (async () => {
        await startTask?.catch(() => {});
        const closed = server.listening
          ? new Promise<void>((resolve, reject) =>
              server.close((error) => (error ? reject(error) : resolve())),
            )
          : Promise.resolve();
        server.closeAllConnections();
        await Promise.all([closed, handlerStopped]);
      })();
      return stopTask;
    },
  };
}
