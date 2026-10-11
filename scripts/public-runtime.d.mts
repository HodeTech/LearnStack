export function publicApiEnvironment(
  values: Record<string, string>,
  configuration: { apiOrigin: string; secret: string },
  base?: Partial<NodeJS.ProcessEnv>,
): NodeJS.ProcessEnv;
export function publicWebEnvironment(
  values: Record<string, string>,
  privateRoot: string,
  base?: Partial<NodeJS.ProcessEnv>,
): NodeJS.ProcessEnv;
