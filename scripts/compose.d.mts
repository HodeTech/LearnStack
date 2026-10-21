export const repositoryRoot: string;
export function readComposeEnvironmentFile(path: string): string;
export function composeSelectionFailure(error: unknown): string | undefined;
export function composeSelection(
  mode: string,
  environment?: Partial<NodeJS.ProcessEnv>,
  root?: string,
  commandArgs?: readonly string[],
): {
  readonly mode: string;
  readonly project: string;
  readonly file: string;
  readonly cwd: string;
  readonly env: NodeJS.ProcessEnv;
  readonly args: readonly string[];
};
