export const repositoryRoot: string;
export function readComposeEnvironmentFile(path: string): string;
export function composeSelection(
  mode: string,
  environment?: Partial<NodeJS.ProcessEnv>,
  root?: string,
): {
  readonly mode: string;
  readonly project: string;
  readonly file: string;
  readonly cwd: string;
  readonly env: NodeJS.ProcessEnv;
  readonly args: readonly string[];
};
