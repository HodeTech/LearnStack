export function isApiListenerEvent(line: string, origin: string): boolean;
export function demoFailureMessage(error: unknown): string;
export function validateCertificate(
  certificate: Buffer,
  privateKey: Buffer,
  hosts: string[],
  now?: number,
): import('node:crypto').X509Certificate;

export function disposeDemoResources(options: {
  processes: Pick<import('./demo-process.mjs').DemoProcesses, 'dispose'>;
  finish?: () => void | Promise<void>;
  teardown?: () => void | Promise<void>;
  removePrivate: () => void | Promise<void>;
}): Promise<void>;
export function startDemo(mode: 'local' | 'ci'): Promise<{
  mode: 'local' | 'ci';
  source?: import('./demo-source.mjs').DemoSource;
  manifest: DemoLane[];
  configuration: import('../frontend/apps/web/src/server/ingress').PublicServerConfiguration;
  ca: string[] | Buffer;
  leaf: import('node:crypto').X509Certificate;
  privateRoot: string;
  observation: {
    apiReady: boolean;
    webReady: boolean;
    counts: Map<string, number>;
    malformed: boolean;
  };
  completions: import('./demo-observation.mjs').CompletionState;
  processes: import('./demo-process.mjs').DemoProcesses;
  signal: AbortSignal;
  dispose(): Promise<{
    passed: true;
    milliseconds: number;
    processesStopped: true;
    composeEmpty: boolean;
    privateFilesRemoved: boolean;
    environmentRemoved: boolean;
    lockRemoved: boolean;
  }>;
  compose(args: string[], options?: { timeout?: number; capture?: boolean }): Promise<string>;
  signalRequested(): boolean;
  setupMilliseconds: number;
}>;

interface DemoLane {
  host: string;
  locale: string;
  displayName: string;
  courseTitle: string;
  lessonTitle: string;
  catalogUrl: string;
  courseUrl: string;
  lessonUrl: string;
  fields: { name: string; label: string; value: string }[];
  theme: Record<string, string>;
  excludedTitles: string[];
  excludedContent: string[];
}
