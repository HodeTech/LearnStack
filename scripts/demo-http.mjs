import { request } from 'node:https';
import { createServer } from 'node:net';

export async function boundedBody(response, maximum) {
  const reader = response.body?.getReader();
  if (!reader) throw new Error('Missing response body');
  const chunks = [];
  let size = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > maximum) throw new Error('Response exceeds bound');
      chunks.push(Buffer.from(value));
    }
    return Buffer.concat(chunks).toString('utf8');
  } finally {
    await reader.cancel();
    reader.releaseLock();
  }
}

export function vacant(port) {
  return new Promise((resolve, reject) => {
    const server = createServer();
    server.once('error', () =>
      reject(new Error(`Port ${port} is occupied; stop its owner before make demo`)),
    );
    server.listen(port, '127.0.0.1', () =>
      server.close((error) => (error ? reject(error) : resolve())),
    );
  });
}

/** Certificate validation and SNI keep the public hostname; only CI resolves it locally. */
export function demoRequest(url, { ca, signal, loopback = false, maximum = 8 * 1024 * 1024 }) {
  return new Promise((resolve, reject) => {
    const target = new URL(url);
    const req = request(
      target,
      {
        ca,
        rejectUnauthorized: true,
        signal: AbortSignal.any([signal, AbortSignal.timeout(10000)]),
        ...(loopback
          ? {
              lookup: (_name, options, callback) =>
                options.all
                  ? callback(null, [{ address: '127.0.0.1', family: 4 }])
                  : callback(null, '127.0.0.1', 4),
            }
          : {}),
      },
      (response) => {
        const chunks = [];
        let size = 0;
        response.on('data', (chunk) => {
          size += chunk.length;
          if (size > maximum) response.destroy(new Error('Response exceeds bound'));
          else chunks.push(chunk);
        });
        response.once('error', reject);
        response.once('end', () =>
          resolve({
            status: response.statusCode,
            headers: response.headers,
            body: Buffer.concat(chunks).toString('utf8'),
          }),
        );
      },
    );
    req.once('error', reject);
    req.end();
  });
}
