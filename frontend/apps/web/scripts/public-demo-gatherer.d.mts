export class PublicDemoDocument {
  startInstrumentation(context: unknown): Promise<void>;
  getArtifact(context: unknown): Promise<unknown>;
}
export const demoLighthouseConfiguration: unknown;
export function configurationCensus(config: unknown): unknown;
export function resolvedDemoCensus(flags: unknown): Promise<unknown>;

export class InitialJavaScriptGzip {
  static readonly meta: {
    id: string;
    title: string;
    description: string;
    scoreDisplayMode: string;
    requiredArtifacts: string[];
  };
  static audit(artifacts: { PublicDemoDocument: { gzip: { total: number } } }): {
    score: number;
    numericValue: number;
    numericUnit: string;
  };
}
