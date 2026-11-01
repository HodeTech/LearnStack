export function createFrameworkDevelopmentPlugin(options: {
  appRoot: string;
  kind: 'client' | 'server' | 'edge';
}): { apply(compiler: unknown): void };
