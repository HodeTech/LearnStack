import type { AdminCopy, DataRequestUiCopy } from '@/i18n/administration-catalogues';
import type {
  DataRequestAdministrationRefusal,
  DataRequestAdministrationResult,
  PageDataRequest,
} from '@/server/auth/page-contract';

/**
 * ADR-0106 §§ 6–8, 11 for the administrator's pages and recovery document. Every sentence is a literal catalogue
 * key, so the catalogue census reads it; the person's own catalogue (`dataRequest`) is used only where its wording is
 * exactly right for an administrator too, and `admin` holds the administrator's wording everywhere else.
 */
export type DataRequestState = PageDataRequest['status'] | 'due';
type Request = Pick<
  PageDataRequest,
  'kind' | 'origin' | 'status' | 'executeAt' | 'requestedByYou' | 'artifact'
>;

/** An erasure past its window but not yet run reads "starting soon", exactly as on the person's own page. */
export function dataRequestDisplayState(
  item: Pick<Request, 'kind' | 'status' | 'executeAt'>,
  preparedAt: string,
): DataRequestState {
  return item.kind === 'erasure' &&
    item.status === 'scheduled' &&
    item.executeAt !== null &&
    Date.parse(preparedAt) >= Date.parse(item.executeAt)
    ? 'due'
    : item.status;
}
/** Only the administrator who filed a request may act on it (the API answers 404 to anyone else). */
const filedByCaller = (item: Request) => item.origin === 'administrator' && item.requestedByYou;
/** ADR-0106 § 11: an export until it starts, an erasure until its window ends. */
export function administratorMayCancel(item: Request, preparedAt: string): boolean {
  if (!filedByCaller(item)) return false;
  if (item.kind === 'export') return item.status === 'requested';
  return item.kind === 'erasure' && dataRequestDisplayState(item, preparedAt) === 'scheduled';
}
/** ADR-0107 § 4: "the requesting administrator may revoke an unexpired artifact". */
export function administratorMayRevoke(item: Request): boolean {
  return filedByCaller(item) && item.kind === 'export' && item.artifact?.readable === true;
}
export function dataRequestKindLabel(
  kind: PageDataRequest['kind'],
  admin: AdminCopy,
  dataRequest: DataRequestUiCopy,
): string {
  switch (kind) {
    case 'export':
      return admin('data_requests.kind.export');
    case 'erasure':
      return admin('data_requests.kind.erasure');
    case 'closure':
      // The API never shows an administrator a closure; the type admits it, so its neutral name is kept.
      return dataRequest('kind.closure');
  }
}
export function dataRequestStatusLabel(state: DataRequestState, dataRequest: DataRequestUiCopy) {
  switch (state) {
    case 'requested':
      return dataRequest('status.requested');
    case 'scheduled':
      return dataRequest('status.scheduled');
    case 'due':
      return dataRequest('status.due');
    case 'fulfilling':
      return dataRequest('status.fulfilling');
    case 'awaiting_privileged_step':
      return dataRequest('status.awaiting_privileged_step');
    case 'completed':
      return dataRequest('status.completed');
    case 'blocked':
      return dataRequest('status.blocked');
    case 'failed':
      return dataRequest('status.failed');
    case 'cancelled':
      return dataRequest('status.cancelled');
  }
}
/** The presentational glyph beside a status; the status text carries the meaning. */
export function dataRequestStateIcon(
  state: DataRequestState,
): 'waiting' | 'working' | 'person' | 'done' | 'stopped' | 'problem' {
  switch (state) {
    case 'completed':
      return 'done';
    case 'cancelled':
      return 'stopped';
    case 'blocked':
    case 'failed':
      return 'problem';
    case 'awaiting_privileged_step':
      return 'person';
    case 'fulfilling':
    case 'due':
      return 'working';
    case 'requested':
    case 'scheduled':
      return 'waiting';
  }
}
export function dataRequestFiledBy(item: Request, admin: AdminCopy): string {
  switch (item.origin) {
    case 'self':
      return admin('data_requests.filed.person');
    case 'closure_fanout':
      return admin('data_requests.filed.closure');
    case 'administrator':
      return item.requestedByYou
        ? admin('data_requests.filed.you')
        : admin('data_requests.filed.other');
  }
}
/** The next step a reader of this request can expect, in the administrator's terms. */
export function dataRequestNextStep(
  item: Request,
  state: DataRequestState,
  admin: AdminCopy,
  dataRequest: DataRequestUiCopy,
): string {
  if (item.status === 'cancelled') return dataRequest('next.cancelled');
  if (item.status === 'failed') return dataRequest('next.failed');
  if (item.status === 'blocked') return dataRequest('next.blocked');
  if (item.kind === 'export')
    return item.status === 'completed'
      ? item.artifact?.readable
        ? admin('data_requests.next.export_ready')
        : admin('data_requests.next.export_gone')
      : admin('data_requests.next.export_requested');
  if (item.kind === 'closure') return dataRequestStatusLabel(state, dataRequest);
  if (state === 'due') return dataRequest('next.erasure_due');
  switch (item.status) {
    case 'scheduled':
      // A fan-out erasure is never scheduled; only the person cancels what they filed themselves.
      return item.origin === 'self'
        ? admin('data_requests.next.erasure_scheduled_self')
        : admin('data_requests.next.erasure_scheduled');
    case 'fulfilling':
      return admin('data_requests.next.erasure_fulfilling');
    case 'awaiting_privileged_step':
      return admin('data_requests.next.erasure_awaiting');
    default:
      return dataRequest('next.erasure_completed');
  }
}
export function dataRequestLastRefusal(
  code: NonNullable<PageDataRequest['lastRefusal']>,
  admin: AdminCopy,
): string {
  switch (code) {
    case 'blocked_window':
      return admin('data_requests.last_refusal.blocked_window');
    case 'blocked_hold':
      return admin('data_requests.last_refusal.blocked_hold');
    case 'blocked_last_administrator':
      return admin('data_requests.last_refusal.blocked_last_administrator');
  }
}
export function dataRequestFailure(
  reason: NonNullable<PageDataRequest['failureReason']>,
  admin: AdminCopy,
  dataRequest: DataRequestUiCopy,
): string {
  switch (reason) {
    case 'export_too_large':
      return admin('data_requests.failure.export_too_large');
    case 'execution_failed':
      return dataRequest('failure.execution_failed');
  }
}
export function dataRequestRetainedLabel(
  kind: PageDataRequest['retained'][number]['kind'],
  admin: AdminCopy,
  dataRequest: DataRequestUiCopy,
): string {
  switch (kind) {
    case 'consent_evidence':
      return admin('data_requests.retained.consent_evidence');
    case 'invitation_record':
      return admin('data_requests.retained.invitation_record');
    case 'invitation_envelope':
      return dataRequest('retained.kind.invitation_envelope');
    case 'audit_actor_reference':
      return admin('data_requests.retained.audit_actor_reference');
    case 'audit_entity_reference':
      return admin('data_requests.retained.audit_entity_reference');
    case 'audit_metadata_reference':
      return admin('data_requests.retained.audit_metadata_reference');
    case 'unscoped_audit_rows':
      return dataRequest('retained.kind.unscoped_audit_rows');
    case 'transport_evidence':
      return dataRequest('retained.kind.transport_evidence');
    case 'execution_evidence':
      return dataRequest('retained.kind.execution_evidence');
    case 'provider_event_record':
      return dataRequest('retained.kind.provider_event_record');
    case 'idempotency_cache':
      return dataRequest('retained.kind.idempotency_cache');
    case 'attribution_reference':
      return dataRequest('retained.kind.attribution_reference');
    case 'federation_actor_reference':
      return admin('data_requests.retained.federation_actor_reference');
    case 'request_receipt':
      return dataRequest('retained.kind.request_receipt');
    case 'external_copy':
      return dataRequest('retained.kind.external_copy');
    case 'unserved_group':
      return admin('data_requests.retained.unserved_group');
  }
}
export function dataRequestCollectionLabel(
  collection: string,
  admin: AdminCopy,
  dataRequest: DataRequestUiCopy,
): string {
  return collection === 'audit.entries'
    ? dataRequest('artifact.collection_audit_entries')
    : collection === 'identity.invitations_issued'
      ? admin('data_requests.collection_invitations_issued')
      : dataRequest('artifact.collection_other');
}
/** One sentence per closed refusal of the administrator's routes; `unknown` is a reason this release does not name. */
export function dataRequestRefusalSentence(
  refusal: DataRequestAdministrationRefusal,
  admin: AdminCopy,
  dataRequest: DataRequestUiCopy,
): string {
  switch (refusal) {
    case 'request_already_active':
      return admin('data_requests.refusal.request_already_active');
    case 'blocked_hold':
      return admin('data_requests.refusal.blocked_hold');
    case 'blocked_last_administrator':
      return admin('data_requests.refusal.blocked_last_administrator');
    case 'subject_export_available':
      return dataRequest('refusal.subject_export_available');
    case 'subject_membership_current':
      // The API judges the person's current Membership of the group, which need not be the one named.
      return admin('data_requests.refusal.subject_membership_current');
    case 'subject_is_requester':
      return dataRequest('refusal.subject_is_requester');
    case 'subject_not_admitted':
      return admin('data_requests.refusal.subject_not_admitted');
    case 'snapshot_too_large':
      return admin('data_requests.refusal.snapshot_too_large');
    case 'data_export_unavailable':
      return admin('data_requests.refusal.data_export_unavailable');
    case 'data_request_window_ended':
      return dataRequest('refusal.data_request_window_ended');
    case 'data_request_not_cancellable':
      return dataRequest('refusal.data_request_not_cancellable');
    case 'unknown':
      return admin('data_requests.refusal.unknown');
  }
}
/**
 * Whether a closed result the page was redirected with still describes the request as prepared: the notice and the
 * title prefix are dropped once the request moved on (a reload, a restored or a shared URL keeps the query).
 */
export function dataRequestResultCurrent(
  result: DataRequestAdministrationResult,
  item: Pick<PageDataRequest, 'kind' | 'status' | 'executeAt' | 'artifact'>,
  preparedAt: string,
): boolean {
  switch (result) {
    case 'export':
      return (
        item.kind === 'export' &&
        (item.status === 'requested' ||
          item.status === 'fulfilling' ||
          (item.status === 'completed' && item.artifact?.readable === true))
      );
    case 'erasure':
      return item.kind === 'erasure' && dataRequestDisplayState(item, preparedAt) === 'scheduled';
    case 'cancelled':
      return item.status === 'cancelled';
    case 'revoked':
      return item.kind === 'export' && item.artifact !== null && !item.artifact.readable;
  }
}
export function dataRequestResultSentence(
  result: DataRequestAdministrationResult,
  admin: AdminCopy,
  dataRequest: DataRequestUiCopy,
): string {
  switch (result) {
    case 'export':
      return admin('data_requests.result.export');
    case 'erasure':
      return admin('data_requests.result.erasure');
    case 'cancelled':
      return dataRequest('result.cancelled');
    case 'revoked':
      return admin('data_requests.result.revoked');
  }
}
