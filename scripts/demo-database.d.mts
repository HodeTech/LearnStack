export function validateDemoDatabaseConfiguration(values: Record<string, string> | null | undefined): void;
export function authorizationSourceEnvironment(values: Record<string, string> | null | undefined): Record<string, string>;
export function developmentSeederRuntimeEnvironment(
  values: Record<string, string>,
  base?: Partial<NodeJS.ProcessEnv>,
): NodeJS.ProcessEnv;
export function publicSeederEnvironment(
  applicationConnectionString?: string,
  base?: Partial<NodeJS.ProcessEnv>,
): NodeJS.ProcessEnv;
