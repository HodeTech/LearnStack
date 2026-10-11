export interface DemoToolchain {
  lighthouse: string;
  playwright: string;
  chromiumRevision: string;
  browserVersion: string;
}
export function readDemoToolchain(): Readonly<DemoToolchain>;
export function toolchainVersions(
  pins: { lighthouse: string; playwright: string },
  installed: {
    lighthouse: string;
    playwright: string;
    core: string;
    browsers: Array<{ name: string; revision: string; browserVersion?: string }>;
  },
): Readonly<DemoToolchain>;
