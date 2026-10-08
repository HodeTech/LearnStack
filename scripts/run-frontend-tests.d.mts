export function discoverTestPackages(
  workspaceRoot: string,
): Promise<Array<{ path: string; name: string; files: string[] }>>;
export function validateTestReport(report: unknown): number;
export function runTestWorkspaces(
  workspaceRoot: string,
  output?: (chunk: Uint8Array) => void,
): Promise<{ packages: number; tests: number }>;
