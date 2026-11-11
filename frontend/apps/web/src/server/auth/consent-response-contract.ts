import type {
  ConsentHistory,
  ConsentScopes,
  ConsentReceipt,
} from '@learnstack/sdk/administration-server';

import type { NativeConsentSnapshot } from './page-contract.js';
export type NativeConsentResponseLease = Readonly<{
  signal: AbortSignal;
  cleaned: Promise<void>;
  pin: (assert: () => void, owner: object, expiresInMs: number) => void;
  publish: (value: NativeConsentSnapshot) => NativeConsentSnapshot;
}>;
export type NativeConsentData = Readonly<{
  scopes: ConsentScopes;
  history: ConsentHistory | null;
  membershipId: string | null;
  csrfToken: string;
  formKeys: readonly string[];
}>;
export type NativeConsentReceipt = ConsentReceipt;
