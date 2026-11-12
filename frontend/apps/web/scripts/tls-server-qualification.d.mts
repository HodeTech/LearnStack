export function qualifyTlsServer(options: {
  certificate: string | Buffer;
  privateKey: string | Buffer;
  ca: string | Buffer;
  hostname: string;
  signal: AbortSignal;
}): Promise<void>;
