import { createHash } from 'node:crypto';

export const customFieldBrowserExactText = Object.freeze({
  carriage: 'a\rb',
  crlf: 'a\r\nb',
  enum: 'enum\r\nmember',
  leading: '\nstart\n',
  'line-feed': 'a\nb',
  mixed: 'a\rb\nc\r\nd',
  slashes: 'C:\\new\\records',
});

export const customFieldBrowserNoticeHash = createHash('sha256')
  .update('learnstack.membership-field-consent.notice.v1\0')
  .update('Fixture purpose notice')
  .digest('hex');

// Finite fake API state for the existing HTTPS/native/Next browser owner.
// This supplies transport/UI examples; real authorization, transactions and RLS have separate proof.
export function createCustomFieldBrowserFixture({
  success,
  pageInfo,
  member,
  noticeHash,
  exactText,
}) {
  const uuid = (index) => '019a154b-0000-7000-8000-' + String(index).padStart(12, '0');
  const labels = (text) => ({ en: text, tr: text, 'tr-TR': text });
  const envelope = (value) => ({
    $schema: 'https://json-schema.org/draft/2020-12/schema',
    type: 'object',
    properties: { value },
    required: ['value'],
    additionalProperties: false,
  });
  const definitions = [];
  const stored = new Map();
  let sequence = 1;
  let granted = false;
  let profileReadOnly = false;
  let next = 200;
  function seed(index, key, schema, status, label, sensitive = false) {
    const definition = {
      definitionId: uuid(index),
      targetEntity: 'Membership',
      key,
      schemaVersion: 1,
      schema: envelope(schema),
      labels: labels(label),
      piiCategory: sensitive ? 'Sensitive' : 'None',
      isRequired: false,
      sort: index,
      purposeKey: sensitive ? 'fixture-purpose' : null,
      notices: sensitive ? labels('Fixture purpose notice') : null,
      formLabels: null,
      status,
      activeRevision: null,
      version: '0',
      isDiscarded: false,
      maximumValueBytes: 1024,
    };
    definitions.push(definition);
    return definition;
  }
  const complex = seed(
    101,
    'fixture-list',
    { type: 'array', items: { type: 'boolean' }, minItems: 0, maxItems: 3 },
    'Active',
    'Fixture list',
  );
  complex.formLabels = [{ location: '/0', labels: labels('List answer'), choices: null }];
  const sensitive = seed(
    102,
    'fixture-sensitive',
    { type: 'string', minLength: 1, maxLength: 32 },
    'Active',
    'Fixture sensitive',
    true,
  );
  const retired = seed(
    103,
    'fixture-retired',
    { type: 'boolean' },
    'Deprecated',
    'Fixture retired',
  );
  const textKeys = Object.keys(exactText).sort();
  const exact = seed(
    104,
    'fixture-exact-text',
    {
      type: 'object',
      properties: Object.fromEntries(
        textKeys.map((key) => [
          key,
          {
            type: 'string',
            minLength: 1,
            maxLength: 24,
            ...(key === 'enum' ? { enum: [exactText.enum, 'plain-choice'] } : {}),
          },
        ]),
      ),
      required: textKeys,
      additionalProperties: false,
    },
    'Active',
    'Fixture exact text',
  );
  exact.maximumValueBytes = 2048;
  exact.formLabels = textKeys.map((key, index) => ({
    location: '/' + index,
    labels: labels(key),
    choices: key === 'enum' ? [labels('Exact choice'), labels('Plain choice')] : null,
  }));
  stored.set(exact.definitionId, { value: structuredClone(exactText) });
  stored.set(sensitive.definitionId, { value: 'BROWSER_WITHHELD_CANARY' });
  stored.set(retired.definitionId, { value: true });
  const exactAuthorLabel = 'Fixture exact\r\nlabel';
  const exactAuthorNotice = 'Purpose\r\nnotice\nwith\rmixed endings';
  const exactAuthor = seed(
    105,
    'fixture-exact-authoring',
    { type: 'string', minLength: 1, maxLength: 24 },
    'Draft',
    exactAuthorLabel,
    true,
  );
  exactAuthor.notices = labels(exactAuthorNotice);
  const evidenceId = () => uuid(400 + sequence);
  const current = () => ({
    evidenceId: evidenceId(),
    sequence: String(sequence),
    isGranted: granted,
  });
  const details = () => ({
    ...current(),
    membershipId: member.membershipId,
    definitionId: sensitive.definitionId,
    definitionVersion: 1,
    purposeKey: sensitive.purposeKey,
    noticeLocale: 'en',
    noticeHash: noticeHash,
    noticeText: 'Fixture purpose notice',
    labels: sensitive.labels,
  });
  const definitionView = (definition) => ({
    ...definition,
    activeRevision: (() => {
      const active = definitions.find(
        (item) => item.key === definition.key && item.status === 'Active',
      );
      return active ? { id: active.definitionId, version: active.version } : null;
    })(),
  });
  const fields = () => ({
    membershipId: member.membershipId,
    version: member.version,
    fields: definitions
      .filter((definition) => definition.status === 'Active' || stored.has(definition.definitionId))
      .map((definition) => {
        const retained = stored.has(definition.definitionId);
        const withheld = retained && definition.piiCategory === 'Sensitive' && !granted;
        return {
          conceptKey: definition.key,
          definitionId: definition.definitionId,
          schemaVersion: definition.schemaVersion,
          state: withheld
            ? 'Withheld'
            : retained
              ? definition.status === 'Active'
                ? 'Current'
                : 'Retired'
              : 'Missing',
          isRequired: definition.isRequired,
          envelope: withheld ? null : (stored.get(definition.definitionId) ?? null),
          consent: definition.piiCategory === 'Sensitive' ? current() : null,
          definition: {
            schema: definition.schema,
            labels: definition.labels,
            piiCategory: definition.piiCategory,
            sort: definition.sort,
            purposeKey: definition.purposeKey,
            notices: definition.notices,
            formLabels: definition.formLabels,
            status: definition.status,
            noticeHashes:
              definition.piiCategory === 'Sensitive'
                ? Object.fromEntries(
                    Object.keys(definition.notices).map((locale) => [locale, noticeHash]),
                  )
                : null,
          },
        };
      }),
  });
  const parse = (value) => (typeof value === 'string' ? JSON.parse(value) : value);
  const refused = () => ({ kind: 'invalid-request' });
  return {
    get profileReadOnly() {
      return profileReadOnly;
    },
    restoreProfilePermissionForFixture() {
      profileReadOnly = false;
    },
    call(operation, binding) {
      const definition =
        operation.id && definitions.find((item) => item.definitionId === operation.id);
      switch (operation.kind) {
        case 'listCustomFieldDefinitions':
          return success({
            items: definitions
              .filter((item) => !operation.status || item.status === operation.status)
              .map(definitionView)
              .slice(0, 20),
            pageInfo,
          });
        case 'getCustomFieldDefinition':
          return definition ? success(definitionView(definition), true) : refused();
        case 'listCustomFieldRevisions':
          return definition
            ? success({
                items: definitions
                  .filter((item) => item.key === definition.key)
                  .map(definitionView),
                pageInfo,
              })
            : refused();
        case 'getOwnCustomFields':
          return success(fields(), true);
        case 'getMembershipCustomFields':
          return operation.id === member.membershipId ? success(fields(), true) : refused();
        case 'registerCustomFieldDefinition': {
          const input = parse(operation.input);
          const created = {
            ...input,
            definitionId: uuid(++next),
            status: 'Draft',
            version: '0',
            isDiscarded: false,
            activeRevision: null,
            maximumValueBytes: 1024,
          };
          definitions.push(created);
          if (input.key === 'browser-boolean') profileReadOnly = true;
          return {
            ...success({ id: created.definitionId, version: created.version }, true),
            status: 201,
            location: '/api/v1/customization/custom-field-definitions/' + created.definitionId,
          };
        }
        case 'reviseCustomFieldDefinition':
          if (
            !definition ||
            definition.status !== 'Draft' ||
            definition.version !== operation.version
          )
            return refused();
          {
            const input = parse(operation.input);
            if (
              definition.key.startsWith('browser-') &&
              (JSON.stringify(input.schema) !== JSON.stringify(definition.schema) ||
                JSON.stringify(input.formLabels) !== JSON.stringify(definition.formLabels))
            )
              return refused();
            if (
              definition === exactAuthor &&
              (JSON.stringify(input.schema) !== JSON.stringify(exactAuthor.schema) ||
                input.formLabels !== null ||
                !input.labels ||
                !Object.keys(input.labels).length ||
                !Object.values(input.labels).every((label) => label === exactAuthorLabel) ||
                !input.notices ||
                !Object.keys(input.notices).length ||
                !Object.values(input.notices).every((notice) => notice === exactAuthorNotice))
            )
              return refused();
            Object.assign(definition, input);
          }
          definition.version = String(BigInt(definition.version) + 1n);
          return success({ id: definition.definitionId, version: definition.version }, true);
        case 'publishCustomFieldDefinition': {
          if (
            !definition ||
            definition.status !== 'Draft' ||
            definition.isDiscarded ||
            definition.version !== operation.version
          )
            return refused();
          const incumbent = definitions.find(
            (item) => item.key === definition.key && item.status === 'Active',
          );
          if (
            (incumbent?.definitionId ?? null) !== operation.expectedIncumbentId ||
            (incumbent?.version ?? null) !== operation.expectedIncumbentVersion
          )
            return refused();
          if (incumbent) {
            incumbent.status = 'Deprecated';
            incumbent.version = String(BigInt(incumbent.version) + 1n);
          }
          definition.status = 'Active';
          definition.version = String(BigInt(definition.version) + 1n);
          return success({ id: definition.definitionId, version: definition.version }, true);
        }
        case 'retireCustomFieldDefinition':
          if (
            !definition ||
            definition.status !== 'Active' ||
            definition.version !== operation.version
          )
            return refused();
          definition.status = 'Deprecated';
          definition.version = String(BigInt(definition.version) + 1n);
          return success({ id: definition.definitionId, version: definition.version }, true);
        case 'discardCustomFieldDefinition':
          if (
            !definition ||
            definition.status !== 'Draft' ||
            definition.version !== operation.version
          )
            return refused();
          definition.isDiscarded = true;
          definition.version = String(BigInt(definition.version) + 1n);
          return success({ id: definition.definitionId, version: definition.version }, true);
        case 'setOwnCustomFields':
        case 'setMembershipCustomFields': {
          if (
            operation.version !== member.version ||
            (operation.id && operation.id !== member.membershipId)
          )
            return refused();
          const patch = parse(operation.patch);
          for (const remove of patch.remove) stored.delete(remove.definitionId);
          for (const set of patch.set) {
            if (
              set.definitionId === exact.definitionId &&
              JSON.stringify(set.envelope.value) !== JSON.stringify(exactText)
            )
              return refused();
            stored.set(set.definitionId, set.envelope);
          }
          member.version = String(BigInt(member.version) + 1n);
          return success({ id: member.membershipId, version: member.version }, true);
        }
        case 'getOwnFieldConsent':
          return success({ consent: details() });
        case 'listOwnConsentScopes':
          return success({
            items: [
              {
                membershipId: member.membershipId,
                tenantId: binding.tenantId,
                organizationId: binding.organizationId,
              },
            ],
            pageInfo,
          });
        case 'listOwnConsentHistory':
          return success({ items: [details()], pageInfo });
        case 'grantOwnFieldConsent':
          if (
            operation.input.definitionId !== sensitive.definitionId ||
            operation.input.definitionVersion !== sensitive.schemaVersion ||
            !Object.hasOwn(sensitive.notices, operation.input.noticeLocale) ||
            operation.input.expectedNoticeHash !== noticeHash ||
            String(operation.input.expectedSequence) !== String(sequence) ||
            operation.input.expectedEvidenceId !== evidenceId() ||
            operation.input.confirmed !== true
          )
            return refused();
          ++sequence;
          granted = true;
          return success(current());
        case 'withdrawOwnFieldConsent':
          if (
            String(operation.input.expectedSequence) !== String(sequence) ||
            operation.input.expectedEvidenceId !== evidenceId() ||
            operation.input.confirmed !== true
          )
            return refused();
          ++sequence;
          granted = false;
          return success(current());
        default:
          return undefined;
      }
    },
  };
}
