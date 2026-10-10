import type { EventEmitter } from 'node:events';

type HmrSocket = Pick<EventEmitter, 'on' | 'off'> & {
  readonly readyState: number;
  terminate(): void;
};
type SocketOptions = {
  readonly ca: string | Buffer;
  readonly servername: 'localhost';
  readonly rejectUnauthorized: true;
  readonly maxPayload: number;
};
export function watchDevelopmentSource(options: {
  readonly app: string;
  readonly certificate: string | Buffer;
  readonly createSocket?: (url: string, options: SocketOptions) => HmrSocket;
}): Promise<{
  afterWrite(writeSource: () => void): Promise<void>;
  close(): void;
}>;
