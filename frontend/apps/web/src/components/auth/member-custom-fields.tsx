/* eslint-disable @next/next/no-html-link-for-pages -- Protected forms use newly admitted document navigation. */
import type { ConsentHistory, MembershipCustomFields } from '@learnstack/sdk/administration-server';

import type { NativePageSnapshot } from '@/server/auth/page-contract';

import { FormIdentityFields, MutationFields } from './admin-forms';
import { FieldAnswer, FieldControls, StaticFieldControls } from './custom-field-controls';
import type { FieldCopy } from './custom-field-controls';
import { CustomFieldNativeForm } from './custom-field-native-form';
import { authoredText, readFieldSchema, fieldControlLabelsSupported } from './custom-field-schema';

type MemberFieldsProps = Readonly<{
  snapshot: NativePageSnapshot;
  fields: MembershipCustomFields;
  canWrite: boolean;
  own: boolean;
  selectedFieldId?: string | null;
  locale: string;
  copy: FieldCopy;
}>;

/** Removal and consent are independent forms; none contain another field's required controls. */
export function MemberCustomFields({
  snapshot,
  fields,
  canWrite,
  own,
  selectedFieldId,
  locale,
  copy,
}: MemberFieldsProps) {
  const defaultLocale = snapshot.site.defaultLocale;
  const ordered = [...fields.fields].sort(
    (left, right) =>
      left.definition.sort - right.definition.sort ||
      (left.conceptKey < right.conceptKey
        ? -1
        : left.conceptKey > right.conceptKey
          ? 1
          : left.schemaVersion - right.schemaVersion),
  );
  const active = ordered.filter((field) => field.definition.status === 'Active');
  const selected = active.find((field) => field.definitionId === selectedFieldId);
  const missingRequired = active.filter((field) => field.isRequired && field.state === 'Missing');
  const editing = missingRequired.length > 0 ? missingRequired : selected ? [selected] : [];
  const eligible = (field: MembershipCustomFields['fields'][number]) =>
    field.definition.piiCategory !== 'Sensitive' || field.consent?.isGranted === true;
  const ready =
    editing.length > 0 &&
    active.filter((field) => field.isRequired).every(eligible) &&
    editing.every(eligible) &&
    editing.every((field) => {
      const schema = readFieldSchema(field.definition.schema);
      return (
        schema !== null &&
        fieldControlLabelsSupported(schema.properties.value, field.definition.formLabels)
      );
    });
  const action = own ? '/api/account/custom-fields' : '/api/studio/membership-custom-fields';
  const destination = own ? '/portal/profile' : '/studio/members/' + fields.membershipId;
  const selectedKeys = new Set(editing.map((field) => field.definitionId));
  const patch = {
    set: editing.map((field) => ({
      conceptKey: field.conceptKey,
      definitionId: field.definitionId,
      schemaVersion: field.schemaVersion,
      envelope: { value: null },
      consentEvidenceId:
        field.definition.piiCategory === 'Sensitive' ? (field.consent?.evidenceId ?? null) : null,
    })),
    remove: [],
  };
  const schemata = editing.map((field) => readFieldSchema(field.definition.schema)!);
  const complex = schemata.some(
    (schema) =>
      schema?.properties.value.type === 'object' || schema?.properties.value.type === 'array',
  );

  return (
    <section aria-labelledby="member-custom-fields-title">
      <h2 id="member-custom-fields-title">{copy.answers}</h2>
      <p>{copy.incomplete}</p>
      <p>{copy.value_budget}</p>
      {missingRequired.length > 0 && <p>{copy.required_first}</p>}
      <p>{copy.remove_hint}</p>
      {!own && <p>{copy.admin_consent}</p>}
      {fields.fields.length === 0 ? (
        <p>{copy.empty}</p>
      ) : (
        <ul className="auth-resource-list">
          {ordered.map((field, index) => {
            const label = authoredText(field.definition.labels, locale, defaultLocale);
            const schema = readFieldSchema(field.definition.schema);
            const pin = {
              conceptKey: field.conceptKey,
              definitionId: field.definitionId,
              schemaVersion: field.schemaVersion,
            };
            const withheld =
              field.state === 'Withheld' || (field.envelope !== null && !eligible(field));
            const notice = field.definition.notices
              ? authoredText(field.definition.notices, locale, defaultLocale)
              : null;
            const grantable =
              own &&
              (active.includes(field) ||
                ['Current', 'Retired', 'Withheld'].includes(field.state)) &&
              field.definition.piiCategory === 'Sensitive' &&
              notice &&
              field.definition.noticeHashes?.[notice.lang];
            return (
              <li key={field.definitionId}>
                <h3 lang={label.lang}>{label.text}</h3>
                <p>
                  {copy.revision}: {field.schemaVersion} ·{' '}
                  {field.isRequired ? copy.required : copy.optional}
                </p>
                <p>
                  {withheld
                    ? copy.withheld
                    : field.state === 'Retired'
                      ? copy.retired
                      : field.state === 'Missing'
                        ? copy.missing
                        : copy.current}
                </p>
                {!withheld &&
                  field.envelope !== null &&
                  (schema ? (
                    <FieldAnswer
                      schema={schema.properties.value}
                      value={field.envelope.value}
                      labels={field.definition.labels}
                      formLabels={field.definition.formLabels}
                      locale={locale}
                      defaultLocale={defaultLocale}
                      copy={copy}
                    />
                  ) : (
                    <p role="alert">{copy.unsupported}</p>
                  ))}
                {own && field.definition.piiCategory === 'Sensitive' && (
                  <section aria-labelledby={'consent-title-' + index}>
                    <h4 id={'consent-title-' + index}>{copy.consent}</h4>
                    <p>{copy.consent_hint}</p>
                    {notice && (
                      <p lang={notice.lang} className="custom-field-notice">
                        {notice.text}
                      </p>
                    )}
                    {field.consent?.isGranted ? <p>{copy.granted}</p> : <p>{copy.withdrawn}</p>}
                    {grantable && !field.consent?.isGranted && (
                      <form action="/api/account/consent-grant" method="post">
                        <FormIdentityFields snapshot={snapshot} index={80 + index} />
                        <input type="hidden" name="definitionId" value={field.definitionId} />
                        <input type="hidden" name="definitionVersion" value={field.schemaVersion} />
                        <input type="hidden" name="noticeLocale" value={notice.lang} />
                        <input
                          type="hidden"
                          name="expectedNoticeHash"
                          value={field.definition.noticeHashes![notice.lang]}
                        />
                        <input
                          type="hidden"
                          name="expectedSequence"
                          value={field.consent?.sequence ?? '0'}
                        />
                        <input
                          type="hidden"
                          name="expectedEvidenceId"
                          value={field.consent?.evidenceId ?? ''}
                        />
                        <label>
                          <input type="checkbox" name="confirmed" value="true" required />{' '}
                          {copy.confirm}
                        </label>
                        <button type="submit">{copy.grant}</button>
                      </form>
                    )}
                    {field.consent?.isGranted && (
                      <form action="/api/account/consent-withdraw" method="post">
                        <FormIdentityFields snapshot={snapshot} index={120 + index} />
                        <input type="hidden" name="membershipId" value={fields.membershipId} />
                        <input type="hidden" name="evidenceId" value={field.consent.evidenceId} />
                        <input
                          type="hidden"
                          name="expectedSequence"
                          value={field.consent.sequence}
                        />
                        <input
                          type="hidden"
                          name="expectedEvidenceId"
                          value={field.consent.evidenceId}
                        />
                        <label>
                          <input type="checkbox" name="confirmed" value="true" required />{' '}
                          {copy.confirm}
                        </label>
                        <button type="submit">{copy.withdraw}</button>
                      </form>
                    )}
                  </section>
                )}
                {canWrite && field.state !== 'Missing' && (
                  <details>
                    <summary>{copy.remove}</summary>
                    <form action={action} method="post">
                      <MutationFields
                        snapshot={snapshot}
                        index={40 + index}
                        version={fields.version}
                        target={own ? undefined : ['membershipId', fields.membershipId]}
                      />
                      <input
                        type="hidden"
                        name="payload"
                        value={JSON.stringify({ set: [], remove: [pin] })}
                      />
                      <label>
                        <input type="checkbox" required /> {copy.confirm}
                      </label>
                      <button type="submit">{copy.remove}</button>
                    </form>
                  </details>
                )}
                {canWrite &&
                  missingRequired.length === 0 &&
                  active.includes(field) &&
                  !selectedKeys.has(field.definitionId) && (
                    <a href={destination + '?field=' + field.definitionId}>
                      {field.state === 'Missing' &&
                      fields.fields.some(
                        (other) =>
                          other.conceptKey === field.conceptKey && other.state !== 'Missing',
                      )
                        ? copy.replace
                        : copy.edit_answer}
                    </a>
                  )}
              </li>
            );
          })}
        </ul>
      )}
      {canWrite && ready && (
        <>
          <CustomFieldNativeForm
            action={action}
            patch={patch}
            schemata={schemata}
            complex={complex}
            copy={copy}
          >
            <MutationFields
              snapshot={snapshot}
              index={30}
              version={fields.version}
              target={own ? undefined : ['membershipId', fields.membershipId]}
            />
            <input type="hidden" name="payload" value={JSON.stringify(patch)} />
            {editing.map((field, index) => {
              const schema = readFieldSchema(field.definition.schema)!;
              return (
                <FieldControls
                  key={field.definitionId}
                  schema={schema.properties.value}
                  labels={field.definition.labels}
                  formLabels={field.definition.formLabels}
                  locale={locale}
                  defaultLocale={defaultLocale}
                  copy={copy}
                  prefix={String(index)}
                  value={
                    eligible(field) && field.state !== 'Withheld'
                      ? field.envelope?.value
                      : undefined
                  }
                />
              );
            })}
            <button type="submit">{copy.save}</button>
          </CustomFieldNativeForm>
          {complex && (
            <noscript>
              <section aria-labelledby="custom-field-native-fallback-title">
                <h3 id="custom-field-native-fallback-title">{copy.no_js_answers}</h3>
                <p>{copy.no_js_answers_hint}</p>
                <form action={action} method="post">
                  <MutationFields
                    snapshot={snapshot}
                    index={31}
                    version={fields.version}
                    target={own ? undefined : ['membershipId', fields.membershipId]}
                  />
                  <input type="hidden" name="payload" value={JSON.stringify(patch)} />
                  {editing.map((field, index) => (
                    <StaticFieldControls
                      key={field.definitionId}
                      schema={schemata[index]!.properties.value}
                      labels={field.definition.labels}
                      formLabels={field.definition.formLabels}
                      locale={locale}
                      defaultLocale={defaultLocale}
                      copy={copy}
                      prefix={String(index)}
                      value={
                        eligible(field) && field.state !== 'Withheld'
                          ? field.envelope?.value
                          : undefined
                      }
                    />
                  ))}
                  <button type="submit">{copy.save}</button>
                </form>
              </section>
            </noscript>
          )}
        </>
      )}
      {own && (
        <p>
          <a href="/account/consents">{copy.retained_entry}</a>
        </p>
      )}
    </section>
  );
}

/** A bounded scope page did not establish whether this Membership has history. */
export function OwnConsentHistoryContinuation({
  membershipId,
  copy,
}: Readonly<{
  membershipId: string;
  copy: FieldCopy;
}>) {
  return (
    <section aria-labelledby="own-consent-history-title">
      <h2 id="own-consent-history-title">{copy.consent_history}</h2>
      <p>{copy.consent_history_incomplete}</p>
      <a href={'/account/consents?membershipId=' + encodeURIComponent(membershipId)}>
        {copy.consent_history_continue}
      </a>
    </section>
  );
}

export function OwnConsentHistory({
  snapshot,
  history,
  locale,
  copy,
}: Readonly<{
  snapshot: NativePageSnapshot;
  history: ConsentHistory;
  locale: string;
  copy: FieldCopy;
}>) {
  return (
    <section aria-labelledby="own-consent-history-title">
      <h2 id="own-consent-history-title">{copy.consent_history}</h2>
      {history.items.length === 0 ? (
        <p>{copy.no_consents}</p>
      ) : (
        <ul className="auth-resource-list">
          {history.items.map((consent, index) => {
            const text = authoredText(consent.labels, locale, snapshot.site.defaultLocale);
            return (
              <li key={consent.evidenceId}>
                <h3 lang={text.lang}>{text.text}</h3>
                <p>
                  {copy.revision}: {consent.definitionVersion} ·{' '}
                  {consent.isGranted ? copy.granted : copy.withdrawn}
                </p>
                <p lang={consent.noticeLocale}>{consent.noticeText}</p>
                {consent.isGranted && (
                  <form action="/api/account/consent-withdraw" method="post">
                    <FormIdentityFields snapshot={snapshot} index={160 + index} />
                    <input type="hidden" name="membershipId" value={consent.membershipId} />
                    <input type="hidden" name="evidenceId" value={consent.evidenceId} />
                    <input type="hidden" name="expectedEvidenceId" value={consent.evidenceId} />
                    <input type="hidden" name="expectedSequence" value={consent.sequence} />
                    <label>
                      <input type="checkbox" name="confirmed" value="true" required />{' '}
                      {copy.confirm}
                    </label>
                    <button type="submit">{copy.withdraw}</button>
                  </form>
                )}
              </li>
            );
          })}
        </ul>
      )}
      <p>
        <a href="/account/consents">{copy.retained_entry}</a>
      </p>
    </section>
  );
}
