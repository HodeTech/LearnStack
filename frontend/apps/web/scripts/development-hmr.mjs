import { createHash } from 'node:crypto';

const MAX_HANDSHAKE_BYTES = 8192;
const WEBSOCKET_GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';

function hasUpgrade(value) {
  return (
    typeof value === 'string' &&
    value.split(',').some((part) => part.trim().toLowerCase() === 'upgrade')
  );
}

/** Only the current Next HMR endpoint is a development WebSocket consumer. */
export function delegateDevelopmentHmr(request, socket, head, sink, deadlineMs = 5000) {
  const key = request.headers['sec-websocket-key'];
  if (
    request.method !== 'GET' ||
    request.url?.split('?')[0] !== '/_next/webpack-hmr' ||
    typeof request.headers.upgrade !== 'string' ||
    request.headers.upgrade.toLowerCase() !== 'websocket' ||
    !hasUpgrade(request.headers.connection) ||
    request.headers['sec-websocket-version'] !== '13' ||
    typeof key !== 'string' ||
    !/^[A-Za-z0-9+/]{22}==$/.test(key) ||
    Buffer.from(key, 'base64').toString('base64') !== key
  ) {
    socket.destroy();
    return;
  }

  const expectedAccept = createHash('sha1')
    .update(key + WEBSOCKET_GUID)
    .digest('base64');
  const originalWrite = socket.write;
  let prefix = Buffer.alloc(0);
  let finished = false;
  const timer = setTimeout(fail, deadlineMs);
  timer.unref();

  function cleanup() {
    if (finished) return;
    finished = true;
    clearTimeout(timer);
    socket.write = originalWrite;
    socket.removeListener('close', cleanup);
    socket.removeListener('error', fail);
    prefix = Buffer.alloc(0);
  }
  function fail() {
    cleanup();
    socket.destroy();
  }

  // Node has no outgoing-data event. Observe only the bounded initial headers,
  // then restore write; an emitted event alone does not prove Next consumed it.
  socket.write = function (chunk, encoding, _callback) {
    const written = Reflect.apply(originalWrite, this, arguments);
    if (finished) return written;
    const bytes =
      typeof chunk === 'string'
        ? Buffer.from(chunk, typeof encoding === 'string' ? encoding : 'utf8')
        : Buffer.from(chunk);
    prefix = Buffer.concat([prefix, bytes.subarray(0, MAX_HANDSHAKE_BYTES + 1 - prefix.length)]);
    const end = prefix.indexOf('\r\n\r\n');
    if (end < 0) {
      if (prefix.length > MAX_HANDSHAKE_BYTES) fail();
      return written;
    }
    if (end + 4 > MAX_HANDSHAKE_BYTES) {
      fail();
      return written;
    }
    const [status, ...lines] = prefix.subarray(0, end).toString('latin1').split('\r\n');
    const headers = new Map();
    for (const line of lines) {
      const at = line.indexOf(':');
      const name = line.slice(0, at).toLowerCase();
      if (at < 1 || headers.has(name)) {
        fail();
        return written;
      }
      headers.set(name, line.slice(at + 1).trim());
    }
    if (
      !/^HTTP\/1\.1 101(?: .*)?$/.test(status) ||
      headers.get('upgrade')?.toLowerCase() !== 'websocket' ||
      !hasUpgrade(headers.get('connection')) ||
      headers.get('sec-websocket-accept') !== expectedAccept
    )
      fail();
    else cleanup();
    return written;
  };
  socket.once('close', cleanup);
  socket.once('error', fail);
  try {
    if (!sink.emit('upgrade', request, socket, head)) fail();
  } catch {
    fail();
  }
}
