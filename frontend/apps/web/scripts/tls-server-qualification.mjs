import { isIP } from 'node:net';
import { duplexPair } from 'node:stream';
import { checkServerIdentity, connect, createSecureContext, TLSSocket } from 'node:tls';

// Exercise the same OpenSSL server-purpose, path and hostname verification used
// by a TLS client, without opening a listener or trusting an unqualified peer.
export async function qualifyTlsServer({ certificate, privateKey, ca, hostname, signal }) {
  const context = createSecureContext({
    cert: certificate,
    key: privateKey,
    minVersion: 'TLSv1.2',
  });
  const [serverEnd, clientEnd] = duplexPair();
  const resources = [];
  const closures = [];
  const own = (stream) => {
    resources.push(stream);
    stream.on('error', () => {});
    closures.push(new Promise((resolve) => stream.once('close', resolve)));
    return stream;
  };
  own(serverEnd);
  own(clientEnd);
  let abort;
  try {
    const server = own(new TLSSocket(serverEnd, { isServer: true, secureContext: context }));
    const client = own(
      connect({
        socket: clientEnd,
        ca,
        rejectUnauthorized: true,
        ...(isIP(hostname) ? {} : { servername: hostname }),
        checkServerIdentity: (_name, cert) => checkServerIdentity(hostname, cert),
        minVersion: 'TLSv1.2',
      }),
    );
    await new Promise((resolve, reject) => {
      abort = () => reject(new Error('TLS qualification interrupted'));
      server.once('error', reject);
      client.once('error', reject);
      client.once('secureConnect', resolve);
      if (signal.aborted) abort();
      else signal.addEventListener('abort', abort, { once: true });
    });
  } finally {
    if (abort) signal.removeEventListener('abort', abort);
    for (const resource of resources) resource.destroy();
    await Promise.all(closures);
  }
}
