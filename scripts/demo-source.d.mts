export interface DemoSource {
  readonly commit: string;
  readonly tree: string;
}

export function demoSource(
  processes: Pick<import('./demo-process.mjs').DemoProcesses, 'run'>,
  root: string,
  remaining: (bound: number) => number,
  expected?: DemoSource,
): Promise<DemoSource>;
