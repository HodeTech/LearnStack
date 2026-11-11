/* eslint-disable @next/next/no-html-link-for-pages -- Protected pages require full document navigation. */
import type { CustomFieldDefinition } from '@learnstack/sdk/administration-server';

import type { NativePageSnapshot, PageView } from '@/server/auth/page-contract';

import { FormIdentityFields, MutationFields } from './admin-forms';
import { CustomFieldBuilder } from './custom-field-builder';
import { FieldControls } from './custom-field-controls';
import type { FieldCopy } from './custom-field-controls';
import {
  authoredText,
  readFieldSchema,
  hasDisabledFieldTranslations,
  enabledFieldControlLabels,
} from './custom-field-schema';
import { CustomFieldTextEditor } from './custom-field-text-editor';

function stateLabel(definition: CustomFieldDefinition, copy: FieldCopy) {
  return definition.isDiscarded
    ? copy.discarded
    : definition.status === 'Draft'
      ? copy.draft
      : definition.status === 'Active'
        ? copy.active
        : copy.deprecated;
}
function Author({
  snapshot,
  initial,
  successor,
  locale,
  copy,
}: Readonly<{
  snapshot: NativePageSnapshot;
  initial?: CustomFieldDefinition;
  successor?: boolean;
  locale: string;
  copy: FieldCopy;
}>) {
  const revise = initial !== undefined && !successor;
  const valueSchema = initial ? readFieldSchema(initial.schema)?.properties.value : undefined;
  const scalar =
    !initial ||
    (valueSchema !== undefined &&
      valueSchema.type !== 'object' &&
      valueSchema.type !== 'array' &&
      !(valueSchema.type === 'string' && valueSchema.enum !== undefined));
  return (
    <>
      <CustomFieldBuilder
        initial={initial}
        successor={successor}
        csrfToken={snapshot.session.csrfToken}
        formKey={snapshot.formKeys[10]!}
        locales={snapshot.site.enabledLocales}
        defaultLocale={snapshot.site.defaultLocale}
        locale={locale}
        copy={copy}
      />
      <noscript>
        {initial && hasDisabledFieldTranslations(initial, snapshot.site.enabledLocales) && (
          <p className="auth-notice">{copy.disabled_translations_hint}</p>
        )}
        <p>
          {copy.default_locale}: {snapshot.site.defaultLocale}
        </p>
        <p>{copy.no_js_builder}</p>
        {scalar ? (
          <form
            className="custom-field-native-scalar"
            action={revise ? '/api/studio/custom-field-revise' : '/api/studio/custom-field-create'}
            method="post"
          >
            {revise ? (
              <MutationFields
                snapshot={snapshot}
                index={11}
                version={initial.version}
                target={['definitionId', initial.definitionId]}
              />
            ) : (
              <FormIdentityFields snapshot={snapshot} index={11} />
            )}
            <input
              type="hidden"
              name="authorFormLabels"
              value={JSON.stringify(
                enabledFieldControlLabels(initial?.formLabels, snapshot.site.enabledLocales),
              )}
            />
            <label htmlFor="author-schema-version">{copy.revision}</label>
            <input
              id="author-schema-version"
              name="schemaVersion"
              type="text"
              inputMode="numeric"
              defaultValue={initial ? initial.schemaVersion + (successor ? 1 : 0) : 1}
              readOnly={revise}
              maxLength={10}
            />
            <label htmlFor="author-key">{copy.key}</label>
            <input
              id="author-key"
              name="authorKey"
              maxLength={100}
              defaultValue={initial?.key ?? ''}
              readOnly={Boolean(initial)}
              required
            />
            <label htmlFor="author-type">{copy.type}</label>
            <select id="author-type" name="authorType" defaultValue={valueSchema?.type ?? 'string'}>
              <option value="string">{copy.string}</option>
              <option value="integer">{copy.integer}</option>
              <option value="number">{copy.number}</option>
              <option value="boolean">{copy.boolean}</option>
            </select>
            <label htmlFor="author-minlength">
              {copy.string} · {copy.min}
            </label>
            <input
              id="author-minlength"
              name="authorMinLength"
              defaultValue={valueSchema?.type === 'string' ? valueSchema.minLength : 1}
              maxLength={3}
            />
            <label htmlFor="author-maxlength">
              {copy.string} · {copy.max}
            </label>
            <input
              id="author-maxlength"
              name="authorMaxLength"
              defaultValue={valueSchema?.type === 'string' ? valueSchema.maxLength : 32}
              maxLength={3}
            />
            <label htmlFor="author-minimum">
              {copy.number} · {copy.min}
            </label>
            <input
              id="author-minimum"
              name="authorMinimum"
              defaultValue={
                valueSchema?.type === 'integer' || valueSchema?.type === 'number'
                  ? valueSchema.minimum
                  : -100
              }
              maxLength={128}
            />
            <label htmlFor="author-maximum">
              {copy.number} · {copy.max}
            </label>
            <input
              id="author-maximum"
              name="authorMaximum"
              defaultValue={
                valueSchema?.type === 'integer' || valueSchema?.type === 'number'
                  ? valueSchema.maximum
                  : 100
              }
              maxLength={128}
            />
            <p>{copy.numeric_hint}</p>
            <label htmlFor="author-category">{copy.category}</label>
            <select
              id="author-category"
              name="authorCategory"
              defaultValue={initial?.piiCategory ?? ''}
              required
            >
              <option value="">{copy.choose}</option>
              <option value="None">{copy.none}</option>
              <option value="Identity">{copy.identity}</option>
              <option value="Behaviour">{copy.behaviour}</option>
              <option value="Sensitive">{copy.sensitive}</option>
            </select>
            <label>
              <input name="authorRequired" type="checkbox" defaultChecked={initial?.isRequired} />{' '}
              {copy.required}
            </label>
            <label htmlFor="author-sort">{copy.order}</label>
            <input
              id="author-sort"
              name="authorSort"
              defaultValue={initial?.sort ?? 0}
              maxLength={6}
            />
            <label htmlFor="author-purpose">{copy.purpose}</label>
            <input
              id="author-purpose"
              name="authorPurpose"
              defaultValue={initial?.purposeKey ?? ''}
              maxLength={64}
            />
            {snapshot.site.enabledLocales.map((tag) => (
              <fieldset key={tag}>
                <legend>{tag}</legend>
                <label htmlFor={'author-label-' + tag}>{copy.label}</label>
                <CustomFieldTextEditor
                  id={'author-label-' + tag}
                  name={'authorLabel_' + tag}
                  modeName={'authorTextMode_Label_' + tag}
                  value={initial?.labels[tag] ?? ''}
                  maxBytes={128}
                  required={tag === snapshot.site.defaultLocale}
                  lang={tag}
                  copy={copy}
                />
                <label htmlFor={'author-notice-' + tag}>{copy.notice}</label>
                <CustomFieldTextEditor
                  id={'author-notice-' + tag}
                  name={'authorNotice_' + tag}
                  modeName={'authorTextMode_Notice_' + tag}
                  value={initial?.notices?.[tag] ?? ''}
                  maxBytes={512}
                  lang={tag}
                  copy={copy}
                />
              </fieldset>
            ))}
            <p>{copy.input_budget}</p>
            <button type="submit">{revise ? copy.save_draft : copy.create}</button>
          </form>
        ) : (
          <p>{copy.scalar_editor_unavailable}</p>
        )}
      </noscript>
    </>
  );
}

export function CustomFieldDirectory({
  snapshot,
  view,
  locale,
  copy,
}: Readonly<{
  snapshot: NativePageSnapshot;
  view: Extract<PageView, { kind: 'custom_fields' }>;
  locale: string;
  copy: FieldCopy;
}>) {
  const page = view.definitions;
  return (
    <>
      <p>{copy.capacity}</p>
      {page.items.length === 0 ? (
        <p>{copy.empty}</p>
      ) : (
        <ul className="auth-resource-list">
          {page.items.map((definition) => {
            const text = authoredText(definition.labels, locale, snapshot.site.defaultLocale);
            return (
              <li key={definition.definitionId}>
                <h2 lang={text.lang}>
                  <a href={'/studio/custom-fields/' + definition.definitionId}>{text.text}</a>
                </h2>
                <p>
                  {copy.revision}: {definition.schemaVersion} · {stateLabel(definition, copy)}
                </p>
              </li>
            );
          })}
        </ul>
      )}
      <nav aria-label={copy.title}>
        <a href="/studio/custom-fields">{copy.first}</a>
        {page.pageInfo.hasNext && page.pageInfo.nextCursor && (
          <a
            href={
              '/studio/custom-fields?' + new URLSearchParams({ cursor: page.pageInfo.nextCursor })
            }
          >
            {copy.next}
          </a>
        )}
      </nav>
      {snapshot.scopeKind === 'tenant' && snapshot.capabilities?.canManageCustomFields && (
        <section aria-labelledby="create-definition-title">
          <h2 id="create-definition-title">{copy.create}</h2>
          <Author snapshot={snapshot} locale={locale} copy={copy} />
        </section>
      )}
    </>
  );
}

export function CustomFieldDetails({
  snapshot,
  view,
  locale,
  copy,
}: Readonly<{
  snapshot: NativePageSnapshot;
  view: Extract<PageView, { kind: 'custom_field' }>;
  locale: string;
  copy: FieldCopy;
}>) {
  const definition = view.definition;
  const label = authoredText(definition.labels, locale, snapshot.site.defaultLocale);
  const schema = readFieldSchema(definition.schema);
  const canManage = snapshot.scopeKind === 'tenant' && snapshot.capabilities?.canManageCustomFields;
  const draft = definition.status === 'Draft' && !definition.isDiscarded;
  return (
    <>
      <p>
        <a href="/studio/custom-fields">{copy.back}</a>
      </p>
      <h2 lang={label.lang}>{label.text}</h2>
      <p>
        {copy.revision}: {definition.schemaVersion} · {stateLabel(definition, copy)}
      </p>
      <p>{copy.immutable}</p>
      <p>{copy.capacity}</p>
      {canManage && !definition.isDiscarded && (
        <section aria-labelledby="edit-definition-title">
          <h3 id="edit-definition-title">{draft ? copy.edit : copy.successor}</h3>
          <Author
            snapshot={snapshot}
            initial={definition}
            successor={!draft}
            locale={locale}
            copy={copy}
          />
        </section>
      )}
      {!draft && (
        <section aria-labelledby="retained-preview-title">
          <h3 id="retained-preview-title">{copy.preview}</h3>
          {schema ? (
            <FieldControls
              schema={schema.properties.value}
              labels={definition.labels}
              formLabels={definition.formLabels}
              locale={locale}
              defaultLocale={snapshot.site.defaultLocale}
              copy={copy}
              prefix="retained-preview"
            />
          ) : (
            <p role="alert">{copy.unsupported}</p>
          )}
        </section>
      )}
      {canManage && draft && (
        <section aria-labelledby="publish-definition-title">
          <h3 id="publish-definition-title">{copy.publish}</h3>
          <p>{copy.publish_hint}</p>
          <p>
            {copy.active}:{' '}
            {view.expectedIncumbentId === null ? (
              copy.no_incumbent
            ) : (
              <a href={'/studio/custom-fields/' + view.expectedIncumbentId}>
                {copy.active}
                {view.revisions.items.find((item) => item.definitionId === view.expectedIncumbentId)
                  ? ' · ' +
                    copy.revision +
                    ' ' +
                    view.revisions.items.find(
                      (item) => item.definitionId === view.expectedIncumbentId,
                    )!.schemaVersion
                  : ''}
              </a>
            )}
          </p>
          <form action="/api/studio/custom-field-publish" method="post">
            <MutationFields
              snapshot={snapshot}
              index={0}
              version={definition.version}
              target={['definitionId', definition.definitionId]}
            />
            <input
              type="hidden"
              name="expectedIncumbentId"
              value={view.expectedIncumbentId ?? ''}
            />
            <input
              type="hidden"
              name="expectedIncumbentVersion"
              value={view.expectedIncumbentVersion ?? ''}
            />
            <label>
              <input type="checkbox" required /> {copy.confirm}
            </label>
            <button type="submit">{copy.publish}</button>
          </form>
        </section>
      )}
      {canManage && (draft || definition.status === 'Active') && (
        <details className="auth-danger">
          <summary>{draft ? copy.discard : copy.retire}</summary>
          <form
            action={draft ? '/api/studio/custom-field-discard' : '/api/studio/custom-field-retire'}
            method="post"
          >
            <MutationFields
              snapshot={snapshot}
              index={1}
              version={definition.version}
              target={['definitionId', definition.definitionId]}
            />
            <label>
              <input type="checkbox" required /> {copy.confirm}
            </label>
            <button type="submit">{draft ? copy.discard : copy.retire}</button>
          </form>
        </details>
      )}
      <section aria-labelledby="definition-history-title">
        <h3 id="definition-history-title">{copy.history}</h3>
        <ul>
          {view.revisions.items.map((item) => (
            <li key={item.definitionId}>
              <a href={'/studio/custom-fields/' + item.definitionId}>
                {copy.revision} {item.schemaVersion}: {stateLabel(item, copy)}
              </a>
            </li>
          ))}
        </ul>
        {view.revisions.pageInfo.hasNext && view.revisions.pageInfo.nextCursor && (
          <a
            href={
              '/studio/custom-fields/' +
              definition.definitionId +
              '?' +
              new URLSearchParams({ cursor: view.revisions.pageInfo.nextCursor })
            }
          >
            {copy.next}
          </a>
        )}
      </section>
    </>
  );
}
