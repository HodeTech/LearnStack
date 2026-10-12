export function validateDemoDatabaseConfiguration(values: Record<string, string>): void;
export function publicSeederEnvironment(
  applicationConnectionString?: string,
  base?: Partial<NodeJS.ProcessEnv>,
): NodeJS.ProcessEnv;
