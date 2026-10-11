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
