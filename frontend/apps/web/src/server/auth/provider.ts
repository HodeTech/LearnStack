import 'server-only';

export { createTenantProvider, FEDERATION_ALIAS, FEDERATION_TEST_SCOPE } from './provider-core.js';
export type {
  ProviderIdentity,
  ProviderCredentials,
  LoginTransaction,
  ProviderPurpose,
} from './provider-core.js';
