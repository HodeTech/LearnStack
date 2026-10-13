export function validateDemoDatabaseConfiguration(values: Record<string, string> | null | undefined): void;
export function publicSeederEnvironment(
  applicationConnectionString?: string,
  base?: Partial<NodeJS.ProcessEnv>,
): NodeJS.ProcessEnv;
