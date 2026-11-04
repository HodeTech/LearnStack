export function prepareFrameworkProof(options: {
  sourceApp: string;
  app: string;
  profile?: 'none' | 'ingress' | 'admission' | 'bff';
}): void;
export function frameworkProofBuildArguments(app: string): string[];
export function reuseFrameworkProof(options: { app: string; templateApp: string }): boolean;
export function copyQualifiedFrameworkBuild(options: {
  sourceAppRoot: string;
  targetAppRoot: string;
}): unknown;
