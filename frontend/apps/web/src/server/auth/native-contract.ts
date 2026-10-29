import { BffProviderError } from './provider-error.js';
import type { ProviderFailure } from './provider-error.js';
import type { ProviderIdentity } from './provider.js';

export type NativeBinding = Readonly<{ host: string; source: string; traceparent?: string }>;
export type NativeScope = Readonly<{ tenantId: string; organizationId: string | null }>;
export type Association = Readonly<{ authorityId: string; subjectKey: string; actorId: string }>;
export type NativeSelf =
  | Readonly<{ state: 'missing' }>
  | Readonly<{ state: 'active'; association: Association }>;
export type SelfContext = NativeScope &
  Readonly<{
    tenantName: string;
    organizationName: string | null;
    entryOrigin: string;
    canUseCurrentHost: boolean;
  }>;
export type SelfContexts = Readonly<{
  items: readonly SelfContext[];
  endCursor: string | null;
  hasNextPage: boolean;
}>;
export type NativeSessionApi = Readonly<{
  self(token: string, binding: NativeBinding, signal: AbortSignal): Promise<NativeSelf>;
  bootstrap(token: string, binding: NativeBinding, signal: AbortSignal): Promise<Association>;
  contexts(
    token: string,
    binding: NativeBinding,
    page: Readonly<{ cursor?: string; limit?: number }>,
    signal: AbortSignal,
  ): Promise<SelfContexts>;
  admit(
    token: string,
    binding: NativeBinding & NativeScope,
    signal: AbortSignal,
  ): Promise<Association & NativeScope>;
  verifyAssociation(identity: ProviderIdentity, witness: Association): void;
}>;

/** Only closed classifications leave native transport; upstream payloads never become errors. */
export class NativeApiError extends BffProviderError {
  constructor(
    reason: ProviderFailure,
    readonly status?: number,
    readonly retryAfterSeconds?: number,
  ) {
    super(reason);
    if (
      (status !== undefined && (!Number.isInteger(status) || status < 400 || status > 599)) ||
      (retryAfterSeconds !== undefined &&
        (status !== 429 ||
          !Number.isInteger(retryAfterSeconds) ||
          retryAfterSeconds < 1 ||
          retryAfterSeconds > 86400))
    )
      throw new BffProviderError('configuration');
  }
}
