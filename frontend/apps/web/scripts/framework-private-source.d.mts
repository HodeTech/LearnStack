export type PrivateFrameworkSource = Readonly<{
  frontendRoot: string;
  appRoot: string;
  driverPath: string;
  guardPath: string;
  brokerPath: string;
  inventory: ReadonlyArray<Readonly<{ path: string; bytes: number; sha256: string }>>;
  inputSha256: string;
  totalBytes: number;
}>;

export function createQualifiedPrivateFrameworkSource(options: {
  root: string;
}): PrivateFrameworkSource;
