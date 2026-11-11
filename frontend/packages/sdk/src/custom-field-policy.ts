import { fields, identifier, object, text, version } from './administration-policy.js';
import { customFieldSchema } from './custom-field-schema-policy.js';
export const customKey = (value: unknown): value is string =>
  typeof value === 'string' &&
  value.length <= 100 &&
  /^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(value) &&
  !['constructor', 'prototype', '__proto__'].includes(value);
export const positive = (value: unknown): value is number =>
  typeof value === 'number' && Number.isSafeInteger(value) && value >= 1 && value <= 2147483647;
export const digest = (value: unknown): value is string =>
  typeof value === 'string' && /^[0-9a-f]{64}$/.test(value);
export const localized = (value: unknown, maximum = 512): boolean =>
  object(value) &&
  Object.keys(value).length >= 1 &&
  Object.keys(value).length <= 50 &&
  Object.entries(value).every(
    ([key, item]) =>
      text(key, 35) && text(item, maximum) && new TextEncoder().encode(item).length <= maximum,
  );
const nullableId = (value: unknown) => value === null || identifier(value);
const category = (value: unknown) =>
  ['None', 'Identity', 'Behaviour', 'Sensitive'].includes(value as string);
function formLabels(value: unknown): boolean {
  return (
    value === null ||
    (Array.isArray(value) &&
      value.length <= 111 &&
      value.every(
        (item) =>
          fields(item, ['location', 'labels', 'choices']) &&
          text(item.location, 64) &&
          (item.labels === null || localized(item.labels, 128)) &&
          (item.choices === null ||
            (Array.isArray(item.choices) &&
              item.choices.length <= 20 &&
              item.choices.every((choice) => localized(choice, 128)))),
      ))
  );
}
export function fieldInput(value: unknown): boolean {
  return (
    fields(value, [
      'targetEntity',
      'key',
      'schemaVersion',
      'schema',
      'labels',
      'piiCategory',
      'isRequired',
      'sort',
      'purposeKey',
      'notices',
      'formLabels',
    ]) &&
    value.targetEntity === 'Membership' &&
    customKey(value.key) &&
    positive(value.schemaVersion) &&
    customFieldSchema(value.schema) &&
    new TextEncoder().encode(JSON.stringify(value.schema)).length <= 2048 &&
    localized(value.labels, 128) &&
    category(value.piiCategory) &&
    typeof value.isRequired === 'boolean' &&
    typeof value.sort === 'number' &&
    Number.isSafeInteger(value.sort) &&
    value.sort >= 0 &&
    value.sort <= 32767 &&
    (value.purposeKey === null || (customKey(value.purposeKey) && value.purposeKey.length <= 64)) &&
    (value.notices === null || localized(value.notices)) &&
    formLabels(value.formLabels) &&
    (value.piiCategory === 'Sensitive'
      ? value.purposeKey !== null && value.notices !== null
      : value.purposeKey === null && value.notices === null)
  );
}
export function definition(value: unknown): boolean {
  if (
    !fields(value, [
      'definitionId',
      'targetEntity',
      'key',
      'schemaVersion',
      'status',
      'activeRevision',
      'version',
      'isDiscarded',
      'schema',
      'labels',
      'piiCategory',
      'isRequired',
      'sort',
      'purposeKey',
      'notices',
      'maximumValueBytes',
      'formLabels',
    ])
  )
    return false;
  const {
    definitionId,
    schemaVersion,
    status,
    activeRevision,
    version: revision,
    isDiscarded,
    maximumValueBytes,
    ...input
  } = value;
  return (
    identifier(definitionId) &&
    (activeRevision === null ||
      (fields(activeRevision, ['id', 'version']) &&
        identifier(activeRevision.id) &&
        version(activeRevision.version))) &&
    positive(schemaVersion) &&
    ['Draft', 'Active', 'Deprecated'].includes(status as string) &&
    version(revision) &&
    typeof isDiscarded === 'boolean' &&
    positive(maximumValueBytes) &&
    fieldInput({ ...input, schemaVersion })
  );
}
export function customPage(value: unknown, item: (value: unknown) => boolean): boolean {
  return (
    fields(value, ['items', 'pageInfo']) &&
    Array.isArray(value.items) &&
    value.items.length <= 20 &&
    value.items.every(item) &&
    fields(value.pageInfo, ['nextCursor', 'previousCursor', 'hasNext', 'hasPrevious']) &&
    typeof value.pageInfo.hasNext === 'boolean' &&
    value.pageInfo.previousCursor === null &&
    value.pageInfo.hasPrevious === false &&
    (value.pageInfo.nextCursor === null || text(value.pageInfo.nextCursor, 1024)) &&
    value.pageInfo.hasNext === (value.pageInfo.nextCursor !== null)
  );
}
export function customPatch(value: unknown): boolean {
  if (
    !fields(value, ['set', 'remove']) ||
    !Array.isArray(value.set) ||
    !Array.isArray(value.remove) ||
    value.set.length + value.remove.length < 1 ||
    value.set.length > 20 ||
    value.remove.length > 20
  )
    return false;
  const pin = (item: Record<string, unknown>) =>
    customKey(item.conceptKey) && identifier(item.definitionId) && positive(item.schemaVersion);
  return (
    value.set.every(
      (item) =>
        fields(item, [
          'conceptKey',
          'definitionId',
          'schemaVersion',
          'envelope',
          'consentEvidenceId',
        ]) &&
        pin(item) &&
        fields(item.envelope, ['value']) &&
        item.envelope.value !== null &&
        nullableId(item.consentEvidenceId),
    ) &&
    value.remove.every(
      (item) => fields(item, ['conceptKey', 'definitionId', 'schemaVersion']) && pin(item),
    ) &&
    new Set(value.set.map((item: Record<string, unknown>) => item.conceptKey)).size ===
      value.set.length &&
    new Set(value.set.map((item: Record<string, unknown>) => item.definitionId)).size ===
      value.set.length &&
    new Set(value.remove.map((item: Record<string, unknown>) => item.conceptKey)).size ===
      value.remove.length
  );
}
export function membershipFields(value: unknown): boolean {
  return (
    fields(value, ['membershipId', 'version', 'fields']) &&
    identifier(value.membershipId) &&
    version(value.version) &&
    Array.isArray(value.fields) &&
    value.fields.length <= 40 &&
    value.fields.every(
      (item) =>
        fields(item, [
          'conceptKey',
          'definitionId',
          'schemaVersion',
          'state',
          'isRequired',
          'consent',
          'envelope',
          'definition',
        ]) &&
        customKey(item.conceptKey) &&
        identifier(item.definitionId) &&
        positive(item.schemaVersion) &&
        ['Current', 'Missing', 'Retired', 'Withheld'].includes(item.state as string) &&
        typeof item.isRequired === 'boolean' &&
        (item.consent === null || consentReceipt(item.consent)) &&
        (item.envelope === null ||
          (fields(item.envelope, ['value']) && item.envelope.value !== null)) &&
        (['Missing', 'Withheld'].includes(item.state as string) ? item.envelope === null : true) &&
        fields(item.definition, [
          'schema',
          'labels',
          'piiCategory',
          'sort',
          'purposeKey',
          'notices',
          'formLabels',
          'noticeHashes',
          'status',
        ]) &&
        ['Active', 'Deprecated'].includes(item.definition.status as string) &&
        customFieldSchema(item.definition.schema) &&
        localized(item.definition.labels, 128) &&
        category(item.definition.piiCategory) &&
        typeof item.definition.sort === 'number' &&
        Number.isSafeInteger(item.definition.sort) &&
        item.definition.sort >= 0 &&
        item.definition.sort <= 32767 &&
        (item.definition.purposeKey === null ||
          (customKey(item.definition.purposeKey) && item.definition.purposeKey.length <= 64)) &&
        (item.definition.notices === null || localized(item.definition.notices)) &&
        formLabels(item.definition.formLabels) &&
        (item.definition.noticeHashes === null ||
          (object(item.definition.noticeHashes) &&
            Object.values(item.definition.noticeHashes).every(digest))) &&
        (item.definition.piiCategory !== 'Sensitive' ||
          item.envelope === null ||
          (object(item.consent) && item.consent.isGranted === true)),
    )
  );
}
export function consent(value: unknown): boolean {
  return (
    fields(value, [
      'evidenceId',
      'sequence',
      'isGranted',
      'membershipId',
      'definitionId',
      'definitionVersion',
      'purposeKey',
      'noticeLocale',
      'noticeHash',
      'noticeText',
      'labels',
    ]) &&
    consentReceipt({
      evidenceId: value.evidenceId,
      sequence: value.sequence,
      isGranted: value.isGranted,
    }) &&
    identifier(value.membershipId) &&
    identifier(value.definitionId) &&
    positive(value.definitionVersion) &&
    customKey(value.purposeKey) &&
    text(value.noticeLocale, 35) &&
    digest(value.noticeHash) &&
    localized(value.labels, 128) &&
    text(value.noticeText, 512) &&
    new TextEncoder().encode(value.noticeText).length <= 512
  );
}
export function consentReceipt(value: unknown): boolean {
  return (
    fields(value, ['evidenceId', 'sequence', 'isGranted']) &&
    identifier(value.evidenceId) &&
    version(value.sequence) &&
    typeof value.isGranted === 'boolean'
  );
}
export function consentGrant(value: unknown): boolean {
  return (
    fields(value, [
      'definitionId',
      'definitionVersion',
      'noticeLocale',
      'expectedNoticeHash',
      'expectedSequence',
      'expectedEvidenceId',
      'confirmed',
    ]) &&
    identifier(value.definitionId) &&
    positive(value.definitionVersion) &&
    text(value.noticeLocale, 35) &&
    digest(value.expectedNoticeHash) &&
    predecessor(value)
  );
}
function predecessor(value: Record<string, unknown>): boolean {
  return (
    typeof value.expectedSequence === 'number' &&
    Number.isSafeInteger(value.expectedSequence) &&
    value.expectedSequence >= 0 &&
    nullableId(value.expectedEvidenceId) &&
    value.confirmed === true
  );
}
export function consentWithdrawal(value: unknown): boolean {
  return (
    fields(value, ['evidenceId', 'expectedSequence', 'expectedEvidenceId', 'confirmed']) &&
    identifier(value.evidenceId) &&
    predecessor(value) &&
    (value.expectedSequence as number) >= 1 &&
    identifier(value.expectedEvidenceId)
  );
}
export function scope(value: unknown): boolean {
  const uuid = (value: unknown): value is string =>
    typeof value === 'string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(value) &&
    value !== '00000000-0000-0000-0000-000000000000';
  return (
    fields(value, ['membershipId', 'tenantId', 'organizationId']) &&
    identifier(value.membershipId) &&
    uuid(value.tenantId) &&
    value.tenantId !== '00000000-0000-7000-8000-000000000002' &&
    (value.organizationId === null || uuid(value.organizationId))
  );
}
