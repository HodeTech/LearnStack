export function defaultServices(model: {
  services: Record<string, { profiles?: string[] }>;
}): string[];
export function healthyServices(
  model: {
    services: Record<string, { profiles?: string[]; healthcheck?: { disable?: boolean } }>;
  },
  rows: { Service: string; State: string; Health?: string }[],
  postgresOnly?: boolean,
): boolean;

export function waitCompose(
  selected: ReturnType<typeof import('./compose.mjs').composeSelection>,
  processes: import('./demo-process.mjs').DemoProcesses,
  signal: AbortSignal,
  postgresOnly?: boolean,
): Promise<void>;
