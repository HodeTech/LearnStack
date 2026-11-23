import type { components } from './generated/schema.js';

/** The person's own receipt-safe projection (ADR-0106 § 9): status and counts, never content. */
export type OwnDataRequest = components['schemas']['OwnDataRequestResponse'];
export type OwnDataRequestPage = components['schemas']['OwnDataRequestPage'];
export type DataExportArtifact = components['schemas']['DataExportArtifactResponse'];
export type DataRequestRetained = components['schemas']['DataRequestRetainedResponse'];
export type DataRequestAccepted =
  | components['schemas']['DataRequestAcceptedResponse']
  | components['schemas']['DataErasureAcceptedResponse']
  | components['schemas']['AccountClosureAcceptedResponse'];
/** A sensitive download cursor; it travels only in a request body and never reaches a page, URL or log. */
export type DataExportTicket = components['schemas']['DataExportTicketResponse'];
export type DataExportChunk = components['schemas']['DataExportChunkResponse'];
export type RetainedDataRequestScope = components['schemas']['RetainedDataRequestScopeResponse'];
export type RetainedDataRequestScopePage =
  components['schemas']['RetainedDataRequestScopePageResponse'];
/** The one retained group an entry operation acts in: `tenant` or an organization's canonical UUIDv7. */
export type DataRequestGroup = string;
export type DataRequestKind = OwnDataRequest['kind'];
export type DataRequestStatus = OwnDataRequest['status'];
/**
 * Exact POST ownership of the person's own and retained-entry wrappers (ADR-0106 §§ 9–10, ADR-0107 § 4). The
 * administrator routes belong to the Studio surface and the operator routes to no web surface; none is here.
 */
export const DATA_REQUEST_POST_CONTRACTS = Object.freeze({
  '/api/v1/identity/me/data-requests/exports': 'CreateOwnDataExport',
  '/api/v1/identity/me/data-requests/erasures': 'CreateOwnDataErasure',
  '/api/v1/identity/me/data-requests/closures': 'CreateOwnAccountClosure',
  '/api/v1/identity/me/data-requests/{id}/cancellations': 'CancelOwnDataRequest',
  '/api/v1/identity/me/data-requests/{id}/artifact/revocations': 'RevokeOwnDataExport',
  '/api/v1/identity/me/data-requests/{id}/artifact/tickets': 'MintOwnDataExportTicket',
  '/api/v1/identity/me/data-requests/{id}/artifact/chunks': 'ReadOwnDataExportChunk',
  '/api/v1/identity/data-request-entry/exports': 'CreateRetainedDataExport',
  '/api/v1/identity/data-request-entry/erasures': 'CreateRetainedDataErasure',
  '/api/v1/identity/data-request-entry/closures': 'CreateRetainedAccountClosure',
  '/api/v1/identity/data-request-entry/cancellations': 'CancelRetainedDataRequest',
  '/api/v1/identity/data-request-entry/revocations': 'RevokeRetainedDataExport',
  '/api/v1/identity/data-request-entry/tickets': 'MintRetainedDataExportTicket',
  '/api/v1/identity/data-request-entry/chunks': 'ReadRetainedDataExportChunk',
} as const);
