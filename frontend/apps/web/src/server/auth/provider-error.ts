export type ProviderFailure =
  | 'configuration'
  | 'cancelled'
  | 'deadline'
  | 'transport'
  | 'response'
  | 'identity'
  | 'invalid_grant'
  | 'provider_refused';

/** Upstream messages, causes, URLs and credential values never become diagnostics. */
export class BffProviderError extends Error {
  constructor(readonly reason: ProviderFailure) {
    super(`BFF provider: ${reason}`);
    this.name = 'BffProviderError';
  }
}
