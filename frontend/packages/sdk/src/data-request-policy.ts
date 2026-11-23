import { fields, identifier, text, version } from './administration-policy.js';

/** Closed validation of the subject data-request wire (ADR-0106 §§ 8–9, ADR-0107 §§ 4 and 6). */
export const DATA_REQUEST_KINDS = ['export', 'erasure', 'closure'] as const;
export const DATA_REQUEST_ORIGINS = ['self', 'administrator', 'closure_fanout'] as const;
export const DATA_REQUEST_STATUSES = [
  'requested',
  'scheduled',
  'fulfilling',
  'awaiting_privileged_step',
  'completed',
  'blocked',
  'failed',
  'cancelled',
] as const;
export const DATA_REQUEST_LAST_REFUSALS = [
  'blocked_window',
  'blocked_hold',
  'blocked_last_administrator',
] as const;
export const DATA_REQUEST_FAILURE_REASONS = ['export_too_large', 'execution_failed'] as const;
export const DATA_EXPORT_ARTIFACT_STATUSES = ['available', 'revoked', 'expired', 'purged'] as const;
export const DATA_REQUEST_RETAINED_KINDS = [
  'consent_evidence',
  'invitation_record',
  'invitation_envelope',
  'audit_actor_reference',
  'audit_entity_reference',
  'audit_metadata_reference',
  'unscoped_audit_rows',
  'transport_evidence',
  'execution_evidence',
  'provider_event_record',
  'idempotency_cache',
  'attribution_reference',
  'federation_actor_reference',
  'request_receipt',
  'external_copy',
  'unserved_group',
] as const;
/** ADR-0107 § 4: at most 128 KiB of plaintext per chunk, at most 64 MiB per bundle. */
export const DATA_EXPORT_CHUNK_BYTES = 128 * 1024;
export const DATA_EXPORT_MAXIMUM_BYTES = 64 * 1024 * 1024;
export const DATA_EXPORT_MAXIMUM_CHUNKS = DATA_EXPORT_MAXIMUM_BYTES / DATA_EXPORT_CHUNK_BYTES;

const oneOf = (value: unknown, choices: readonly string[]): value is string =>
  typeof value === 'string' && choices.includes(value);
const integer = (value: unknown, minimum: number, maximum: number): value is number =>
  typeof value === 'number' && Number.isSafeInteger(value) && value >= minimum && value <= maximum;
export function dataRequestInstant(value: unknown): value is string {
  if (
    typeof value !== 'string' ||
    !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(value)
  )
    return false;
  const timestamp = Date.parse(value);
  return (
    Number.isFinite(timestamp) &&
    new Date(timestamp).toISOString().slice(0, 19) === value.slice(0, 19)
  );
}
const nullableInstant = (value: unknown) => value === null || dataRequestInstant(value);
/** `tenant` for the real host's tenant-wide group, or an organization's canonical UUIDv7. */
export function dataRequestGroup(value: unknown): value is string {
  return value === 'tenant' || identifier(value);
}
/** Canonical unpadded base64url of 32 bytes; never a route, query, page or log value. */
export function dataExportTicket(value: unknown): value is string {
  // 43 characters whose last carries two zero bits: exactly 32 bytes, one spelling.
  return typeof value === 'string' && /^[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]$/.test(value);
}

function pageInfo(value: unknown, cursor: number): boolean {
  return (
    fields(value, ['nextCursor', 'previousCursor', 'hasNext', 'hasPrevious']) &&
    (value.nextCursor === null || text(value.nextCursor, cursor)) &&
    value.previousCursor === null &&
    value.hasPrevious === false &&
    typeof value.hasNext === 'boolean' &&
    value.hasNext === (value.nextCursor !== null)
  );
}
function artifact(value: unknown): boolean {
  if (
    !fields(value, [
      'id',
      'status',
      'expiresAt',
      'purgedAt',
      'byteCount',
      'chunkCount',
      'truncations',
      'version',
      'readable',
    ]) ||
    !identifier(value.id) ||
    !oneOf(value.status, DATA_EXPORT_ARTIFACT_STATUSES) ||
    !dataRequestInstant(value.expiresAt) ||
    !nullableInstant(value.purgedAt) ||
    !integer(value.byteCount, 1, DATA_EXPORT_MAXIMUM_BYTES) ||
    !integer(value.chunkCount, 1, DATA_EXPORT_MAXIMUM_CHUNKS) ||
    // Every chunk but the last is full, so the counts agree exactly.
    value.chunkCount !== Math.ceil(value.byteCount / DATA_EXPORT_CHUNK_BYTES) ||
    !version(value.version) ||
    typeof value.readable !== 'boolean' ||
    (value.readable && value.status !== 'available') ||
    !Array.isArray(value.truncations) ||
    value.truncations.length > 32
  )
    return false;
  return value.truncations.every(
    (item) =>
      fields(item, ['collection', 'included', 'total']) &&
      text(item.collection, 65) &&
      /^[a-z][a-z0-9_]*\.[a-z][a-z0-9_]*$/.test(item.collection) &&
      integer(item.included, 0, Number.MAX_SAFE_INTEGER) &&
      integer(item.total, 0, Number.MAX_SAFE_INTEGER) &&
      item.included <= item.total,
  );
}
export function ownDataRequest(value: unknown): boolean {
  if (
    !fields(value, [
      'id',
      'kind',
      'origin',
      'status',
      'createdAt',
      'updatedAt',
      'completedAt',
      'lastRefusal',
      'failureReason',
      'retained',
      'artifact',
      'version',
      'executeAt',
    ]) ||
    !identifier(value.id) ||
    !oneOf(value.kind, DATA_REQUEST_KINDS) ||
    !oneOf(value.origin, DATA_REQUEST_ORIGINS) ||
    !oneOf(value.status, DATA_REQUEST_STATUSES) ||
    !dataRequestInstant(value.createdAt) ||
    !nullableInstant(value.updatedAt) ||
    !nullableInstant(value.completedAt) ||
    (value.completedAt !== null && value.status !== 'completed') ||
    !(value.lastRefusal === null || oneOf(value.lastRefusal, DATA_REQUEST_LAST_REFUSALS)) ||
    !(value.failureReason === null || oneOf(value.failureReason, DATA_REQUEST_FAILURE_REASONS)) ||
    !version(value.version) ||
    !nullableInstant(value.executeAt) ||
    (value.kind === 'export' && value.executeAt !== null) ||
    !(value.artifact === null || (value.kind === 'export' && artifact(value.artifact))) ||
    !Array.isArray(value.retained) ||
    value.retained.length > DATA_REQUEST_RETAINED_KINDS.length
  )
    return false;
  return (
    value.retained.every(
      (item) =>
        fields(item, ['kind', 'count']) &&
        oneOf(item.kind, DATA_REQUEST_RETAINED_KINDS) &&
        integer(item.count, 0, Number.MAX_SAFE_INTEGER),
    ) &&
    new Set(value.retained.map((item) => (item as { kind: string }).kind)).size ===
      value.retained.length
  );
}
export function ownDataRequestPage(value: unknown, maximum: number): boolean {
  return (
    fields(value, ['items', 'pageInfo']) &&
    Array.isArray(value.items) &&
    value.items.length <= maximum &&
    value.items.every(ownDataRequest) &&
    new Set(value.items.map((item) => (item as { id: string }).id)).size === value.items.length &&
    pageInfo(value.pageInfo, 1024)
  );
}
export function retainedScopePage(value: unknown): boolean {
  return (
    fields(value, ['items', 'pageInfo']) &&
    Array.isArray(value.items) &&
    value.items.length <= 20 &&
    value.items.every(
      (item) =>
        fields(item, ['tenantId', 'organizationId']) &&
        identifier(item.tenantId) &&
        (item.organizationId === null || identifier(item.organizationId)),
    ) &&
    new Set(value.items.map((item) => (item as { organizationId: string | null }).organizationId))
      .size === value.items.length &&
    pageInfo(value.pageInfo, 36)
  );
}
const accepted = { export: 'requested', erasure: 'scheduled', closure: 'scheduled' } as const;
export function dataRequestAccepted(value: unknown, kind: keyof typeof accepted): boolean {
  return (
    fields(value, ['id', 'kind', 'status', 'version']) &&
    identifier(value.id) &&
    value.kind === kind &&
    value.status === accepted[kind] &&
    version(value.version)
  );
}
export function dataExportTicketResponse(value: unknown): boolean {
  return (
    fields(value, ['ticket', 'expiresAt', 'chunkCount', 'byteCount']) &&
    dataExportTicket(value.ticket) &&
    dataRequestInstant(value.expiresAt) &&
    integer(value.chunkCount, 1, DATA_EXPORT_MAXIMUM_CHUNKS) &&
    integer(value.byteCount, 1, DATA_EXPORT_MAXIMUM_BYTES) &&
    value.chunkCount === Math.ceil(value.byteCount / DATA_EXPORT_CHUNK_BYTES)
  );
}
/** Canonical padded base64 of 1 to 128 KiB; the relay checks the exact size per index. */
export function dataExportChunkResponse(value: unknown): boolean {
  return (
    fields(value, ['index', 'count', 'bytes']) &&
    integer(value.count, 1, DATA_EXPORT_MAXIMUM_CHUNKS) &&
    integer(value.index, 0, value.count - 1) &&
    typeof value.bytes === 'string' &&
    value.bytes.length >= 4 &&
    value.bytes.length <= 174764 &&
    value.bytes.length % 4 === 0 &&
    /^[A-Za-z0-9+/]*(?:[AQgw]==|[AEIMQUYcgkosw048]=)?$/.test(value.bytes)
  );
}
