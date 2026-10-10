type Response = {
  readonly body: string;
  readonly status: number;
  readonly headers: Readonly<Record<string, string | undefined>>;
};
type Tenant = { readonly host: string; readonly name: string };
type Product = {
  readonly host: string;
  readonly protectedCanaries: readonly string[];
  readonly allCanaries: readonly string[];
  readonly courseTitle: string;
  readonly courseSummary: string;
  readonly lessonTitle: string;
  readonly content: { readonly fields: readonly { readonly value: string }[] };
};
export function absentFromSerializedText(
  text: string,
  values: readonly string[],
  mark?: (index: number) => void,
): void;
export function absentFromWholeResponse(
  response: Pick<Response, 'body'>,
  values: readonly string[],
  mark?: (index: number) => void,
): void;
export function visibleDocument(response: Pick<Response, 'body'>): Document;
export function documentViewport(doc: Document): void;
export function statusDocument(
  response: Response,
  tenant: Tenant,
  locale: string,
  uiLocale: string,
  options: {
    readonly tenants: readonly Tenant[];
    readonly catalogueSentinels?: readonly string[];
    readonly mark?: (part: string) => void;
  },
): Document;
export function productContainment(
  response: Pick<Response, 'body'>,
  tenant: Tenant,
  configuration: { readonly tenants: readonly Tenant[]; readonly product: readonly Product[] },
  catalogueSentinels?: readonly string[],
  mark?: (index: number) => void,
): void;
export function productTheme(
  doc: Document,
  details: {
    readonly theme: {
      readonly primary: string;
      readonly background: string;
      readonly foreground: string;
      readonly muted: string;
    };
    readonly showAttribution: boolean;
    readonly locale: string;
  },
  malformed?: boolean,
  readStylesheet?: (path: string) => string,
): void;
