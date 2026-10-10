type NativeChild = { readonly pid: number; readonly exitCode: number | null };
export function verifyHmrAdmission(options: {
  readonly app: string;
  readonly configuration: { readonly tenants: readonly { readonly name: string }[] };
  readonly checkpoint: (name: string) => Promise<unknown>;
  readonly startNative: (dev: boolean) => Promise<NativeChild>;
  readonly stopNative: (child: NativeChild) => Promise<void>;
  readonly call: (
    path: string,
    options?: {
      readonly headers?: Readonly<Record<string, string>>;
    },
  ) => Promise<{
    readonly status: number;
    readonly headers: Readonly<Record<string, string>>;
    readonly body: string;
  }>;
  readonly observe: (child: NativeChild) => Promise<{
    readonly active: number;
    readonly snapshots: number;
  }>;
  readonly watchSource: () => Promise<{
    readonly afterWrite: (write: () => void) => Promise<void>;
    readonly close: () => void;
  }>;
}): Promise<void>;
