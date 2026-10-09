import type { Server, IncomingMessage } from 'node:http';
import type { Duplex } from 'node:stream';

export function delegateDevelopmentHmr(
  request: IncomingMessage,
  socket: Duplex,
  head: Buffer,
  sink: Server,
  deadlineMs?: number,
): void;
