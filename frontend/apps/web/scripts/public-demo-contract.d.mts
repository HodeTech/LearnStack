export function digest(value: string | Buffer): string;
export function productDomSnapshot(): unknown;
export function admitProduct(
  document: unknown,
  lane: unknown,
  kind: string,
  catalogTitle: string,
): void;
export function decodedSource(body: unknown): string;
export function normalizedJavaScript(
  document: string,
  inventory: unknown,
): {
  scripts: Array<{ id: string; url: string; digest: string; bytes: number; gzip: number }>;
  inline: { count: number; digest: string; bytes: number; gzip: number };
  total: number;
};
export function assertConfiguration(actual: unknown, expected: unknown): void;
export function validateReport(
  lhr: unknown,
  expected: unknown,
  census: unknown,
): Record<string, number>;
export function reportInventory(rows: unknown, expected: unknown): void;
export function validateReportPair(
  row: { reportDigest: string; htmlDigest: string },
  json: string | Buffer,
  html: string | Buffer,
): void;

export function reconcileScriptInventory(
  log: Array<{
    method: string;
    params: { type: string; requestId: string; response: { url: string } };
  }>,
  inventory: Array<{ id: string; url: string }>,
): void;
export function reportAdvisories(
  reports: Array<{ url: string; metrics: Record<string, number> }>,
): Array<{
  url: string;
  metrics: Record<string, { median: number; threshold: number; warning: boolean }>;
}>;
