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
/**
 * An administrator's receipt of a request of the context's group (ADR-0106 §§ 3, 6, 13): the own receipt plus who
 * filed it relative to the caller and the frozen Membership snapshot that names the subject. Never a closure.
 */
export type SubjectDataRequest = components['schemas']['DataRequestResponse'];
export type SubjectDataRequestPage = components['schemas']['DataRequestPage'];
export type DataRequestSubjectInput = components['schemas']['DataExportForSubjectInput'];
/** The one retained group an entry operation acts in: `tenant` or an organization's canonical UUIDv7. */
export type DataRequestGroup = string;
export type DataRequestKind = OwnDataRequest['kind'];
export type DataRequestStatus = OwnDataRequest['status'];
/**
 * Exact POST ownership of the administrator's wrappers (ADR-0106 §§ 3, 10; ADR-0107 § 4): creation for a Membership
 * of the group, and cancellation and revocation of a request the caller filed. No ticket or chunk route exists here:
 * an administrator never receives the bundle.
 */
export const DATA_REQUEST_ADMINISTRATION_POST_CONTRACTS = Object.freeze({
  '/api/v1/identity/data-requests/exports': 'CreateDataExportForSubject',
  '/api/v1/identity/data-requests/erasures': 'CreateDataErasureForSubject',
  '/api/v1/identity/data-requests/{id}/cancellations': 'CancelDataRequest',
  '/api/v1/identity/data-requests/{id}/artifact/revocations': 'RevokeDataExport',
} as const);
/**
 * Exact POST ownership of the person's own and retained-entry wrappers (ADR-0106 §§ 9–10, ADR-0107 § 4). The
 * administrator routes are above and the operator routes belong to no web surface.
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
