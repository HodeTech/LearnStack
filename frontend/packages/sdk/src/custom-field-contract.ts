import type {
  AdministrationCreateOptions,
  AdministrationPageOptions,
  AdministrationReadOptions,
  AdministrationWriteOptions,
} from './administration-contract.js';
export type JsonValue =
  | string
  | number
  | boolean
  | null
  | readonly JsonValue[]
  | { readonly [key: string]: JsonValue };
export type LocalizedText = Readonly<Record<string, string>>;
export type FormLabel = Readonly<{
  location: string;
  labels: LocalizedText | null;
  choices: readonly LocalizedText[] | null;
}>;
export type CustomFieldInput = Readonly<{
  targetEntity: 'Membership';
  key: string;
  schemaVersion: number;
  schema: Readonly<Record<string, JsonValue>>;
  labels: LocalizedText;
  piiCategory: 'None' | 'Identity' | 'Behaviour' | 'Sensitive';
  isRequired: boolean;
  sort: number;
  purposeKey: string | null;
  notices: LocalizedText | null;
  formLabels: readonly FormLabel[] | null;
}>;
export type CustomFieldDefinition = CustomFieldInput &
  Readonly<{
    definitionId: string;
    schemaVersion: number;
    status: 'Draft' | 'Active' | 'Deprecated';
    activeRevision: Readonly<{ id: string; version: string }> | null;
    version: string;
    isDiscarded: boolean;
    maximumValueBytes: number;
  }>;
export type CustomPage<T> = Readonly<{
  items: readonly T[];
  pageInfo: Readonly<{
    nextCursor: string | null;
    previousCursor: null;
    hasNext: boolean;
    hasPrevious: false;
  }>;
}>;
export type CustomFieldPage = CustomPage<CustomFieldDefinition>;
export type CustomFieldReceipt = Readonly<{ id: string; version: string }>;
export type MembershipCustomFields = Readonly<{
  membershipId: string;
  version: string;
  fields: readonly Readonly<{
    conceptKey: string;
    definitionId: string;
    schemaVersion: number;
    state: 'Current' | 'Missing' | 'Retired' | 'Withheld';
    isRequired: boolean;
    consent: ConsentReceipt | null;
    envelope: Readonly<{ value: JsonValue }> | null;
    definition: Pick<
      CustomFieldInput,
      'schema' | 'labels' | 'piiCategory' | 'sort' | 'purposeKey' | 'notices' | 'formLabels'
    > &
      Readonly<{ status: 'Active' | 'Deprecated'; noticeHashes: LocalizedText | null }>;
  }>[];
}>;
export type CustomFieldPatch = Readonly<{
  set: readonly Readonly<{
    conceptKey: string;
    definitionId: string;
    schemaVersion: number;
    envelope: Readonly<{ value: JsonValue }>;
    consentEvidenceId: string | null;
  }>[];
  remove: readonly Readonly<{ conceptKey: string; definitionId: string; schemaVersion: number }>[];
}>;
export type PersonalConsent = Readonly<{
  evidenceId: string;
  sequence: string;
  isGranted: boolean;
  membershipId: string;
  definitionId: string;
  definitionVersion: number;
  purposeKey: string;
  noticeLocale: string;
  noticeHash: string;
  noticeText: string;
  labels: LocalizedText;
}>;
export type ConsentHistory = CustomPage<PersonalConsent>;
export type ConsentScopes = CustomPage<
  Readonly<{ membershipId: string; tenantId: string; organizationId: string | null }>
>;
export type ConsentReceipt = Pick<PersonalConsent, 'evidenceId' | 'sequence' | 'isGranted'>;
export type ConsentGrant = Readonly<{
  definitionId: string;
  definitionVersion: number;
  noticeLocale: string;
  expectedNoticeHash: string;
  expectedSequence: number;
  expectedEvidenceId: string | null;
  confirmed: boolean;
}>;
export type ConsentWithdrawal = Readonly<{
  evidenceId: string;
  expectedSequence: number;
  expectedEvidenceId: string;
  confirmed: boolean;
}>;
export type CustomFieldReadOptions = AdministrationReadOptions;
export type CustomFieldPageOptions = AdministrationPageOptions &
  Readonly<{ status?: 'Draft' | 'Active' | 'Deprecated' }>;
export type CustomFieldCreateOptions = AdministrationCreateOptions;
export type CustomFieldWriteOptions = AdministrationWriteOptions;
