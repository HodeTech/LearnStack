export function boundedBody(response: Response, maximum: number): Promise<string>;
export function vacant(port: number): Promise<void>;
export function demoRequest(
  url: string,
  options: {
    ca: string[] | Buffer;
    signal: AbortSignal;
    loopback?: boolean;
    maximum?: number;
  },
): Promise<{
  status: number;
  headers: import('node:http').IncomingHttpHeaders;
  body: string;
}>;
