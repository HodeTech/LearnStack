import 'server-only';

import { parseCustomFieldJson } from '@learnstack/sdk';

import { getAdministrationCatalogue } from '@/i18n/administration-catalogues';
import { createAuthTranslator } from '@/i18n/auth-catalogues';
import type { NativeActionPage, PageSite } from '@/server/auth/page-contract';
import { isNativeReturnPath } from '@/server/auth/session-path';

import {
  customFieldHidden,
  customFieldOptions,
  escapeCustomFieldHtml,
  renderStaticCustomFieldControls,
} from './custom-field-native-html';
import { fieldControlLabelsSupported, readFieldSchema } from './custom-field-schema';

const memberActions = ['/api/account/custom-fields', '/api/studio/membership-custom-fields'];
const authorActions = ['/api/studio/custom-field-create', '/api/studio/custom-field-revise'];
const lifecycleActions = [
  '/api/studio/custom-field-publish',
  '/api/studio/custom-field-retire',
  '/api/studio/custom-field-discard',
  '/api/account/consent-grant',
  '/api/account/consent-withdraw',
];

/** Transient submitted edits remain in this private response only, never in a receipt or cache. */
export function customFieldMutationContent(
  page: Extract<NativeActionPage, { kind: 'mutation_error' }>,
  locale: string,
  site: PageSite,
): string | null {
  if (
    page.reason !== 'validation' ||
    ![...memberActions, ...authorActions, ...lifecycleActions].includes(page.action)
  )
    return null;
  const { copy } = createAuthTranslator(locale);
  const text = getAdministrationCatalogue(locale).messages.admin.custom_fields;
  const fields = new Map(page.fields);
  const hiddenFields = page.fields.filter(([name]) => !/^(?:answer|presence|count)_/.test(name));
  let controls: string | null = null;
  const escape = escapeCustomFieldHtml;
  let preparedPayload = fields.get('payload') ?? '';
  if (memberActions.includes(page.action) && page.customFieldControls) {
    try {
      const descriptors = page.customFieldControls;
      if (
        descriptors.length >= 1 &&
        descriptors.length <= 20 &&
        descriptors.every((entry, index) => {
          const schema = readFieldSchema(entry.schema);
          return (
            entry.index === index &&
            schema !== null &&
            fieldControlLabelsSupported(schema.properties.value, entry.formLabels)
          );
        })
      ) {
        const submitted = Object.fromEntries(page.fields);
        const raw = !page.fields.some(([name]) => /^(?:answer|presence|count)_/.test(name));
        const patch = raw
          ? (parseCustomFieldJson(preparedPayload) as {
              set: readonly { envelope: { value: unknown } }[];
              remove: readonly unknown[];
            })
          : null;
        if (
          patch &&
          (!Array.isArray(patch.set) ||
            patch.set.length !== descriptors.length ||
            !Array.isArray(patch.remove))
        )
          throw new Error('Prepared form unavailable');
        if (patch)
          preparedPayload = JSON.stringify({
            ...patch,
            set: patch.set.map((operation) => ({ ...operation, envelope: { value: null } })),
          });
        controls = descriptors
          .map((entry, index) => {
            const schema = readFieldSchema(entry.schema)!;
            return renderStaticCustomFieldControls({
              schema: schema.properties.value,
              labels: entry.labels,
              formLabels: entry.formLabels,
              locale,
              defaultLocale: site.defaultLocale,
              copy: text,
              prefix: String(index),
              submitted: raw ? undefined : submitted,
              value: patch?.set[index]?.envelope.value,
            });
          })
          .join('');
      }
    } catch {
      controls = null;
    }
  }

  const scalarAuthor = authorActions.includes(page.action) && fields.has('authorType');
  if (scalarAuthor) {
    const labels: Readonly<Record<string, string>> = {
      schemaVersion: text.revision,
      authorKey: text.key,
      authorMinLength: text.string + ' · ' + text.min,
      authorMaxLength: text.string + ' · ' + text.max,
      authorMinimum: text.number + ' · ' + text.min,
      authorMaximum: text.number + ' · ' + text.max,
      authorSort: text.order,
      authorPurpose: text.purpose,
    };
    controls =
      Object.entries(labels)
        .map(
          ([name, label]) =>
            `<div><label for="${escape('recover-' + name)}">${escape(label)}</label><input id="${escape('recover-' + name)}" name="${escape(name)}" maxlength="128" value="${escape(fields.get(name) ?? '')}"></div>`,
        )
        .join('') +
      `<label for="recover-authorType">${escape(text.type)}</label><select id="recover-authorType" name="authorType">${customFieldOptions(
        [
          { value: 'string', label: text.string },
          { value: 'integer', label: text.integer },
          { value: 'number', label: text.number },
          { value: 'boolean', label: text.boolean },
        ],
        fields.get('authorType') ?? '',
      )}</select>` +
      `<label for="recover-authorCategory">${escape(text.category)}</label><select id="recover-authorCategory" name="authorCategory">${customFieldOptions(
        [
          { value: '', label: text.choose },
          { value: 'None', label: text.none },
          { value: 'Identity', label: text.identity },
          { value: 'Behaviour', label: text.behaviour },
          { value: 'Sensitive', label: text.sensitive },
        ],
        fields.get('authorCategory') ?? '',
      )}</select>` +
      `<label><input type="checkbox" name="authorRequired"${fields.get('authorRequired') === 'on' ? ' checked' : ''}> ${escape(text.required)}</label>` +
      site.enabledLocales
        .map(
          (tag) =>
            `<fieldset><legend>${escape(tag)}</legend><label for="${escape('recover-label-' + tag)}">${escape(text.label)}</label><input id="${escape('recover-label-' + tag)}" name="${escape('authorLabel_' + tag)}" maxlength="128" value="${escape(fields.get('authorLabel_' + tag) ?? '')}"><label for="${escape('recover-notice-' + tag)}">${escape(text.notice)}</label><textarea id="${escape('recover-notice-' + tag)}" name="${escape('authorNotice_' + tag)}" maxlength="512">${escape(fields.get('authorNotice_' + tag) ?? '')}</textarea></fieldset>`,
        )
        .join('');
  }

  const editable = controls !== null || authorActions.includes(page.action);
  const submittedInputs: string[] = [];
  if (memberActions.includes(page.action) && controls === null) {
    if (page.fields.some(([name]) => /^(?:answer|presence|count)_/.test(name))) {
      for (const [name, value] of page.fields)
        if (name.startsWith('answer_') && value !== '') submittedInputs.push(value);
    } else {
      try {
        const original = parseCustomFieldJson(fields.get('payload') ?? '') as {
          set?: readonly { envelope?: { value?: unknown } }[];
        };
        const visit = (value: unknown): void => {
          if (typeof value === 'string' && value !== '') submittedInputs.push(value);
          else if (typeof value === 'number') submittedInputs.push(String(value));
          else if (typeof value === 'boolean') submittedInputs.push(value ? text.yes : text.no);
          else if (Array.isArray(value)) value.forEach(visit);
          else if (value !== null && typeof value === 'object') Object.values(value).forEach(visit);
        };
        if (Array.isArray(original.set))
          original.set.forEach((operation) => visit(operation?.envelope?.value));
      } catch {
        /* Unreadable input grants no inferred answer or field metadata. */
      }
    }
  }

  const proof = hiddenFields
    .filter(
      ([name]) =>
        (controls !== null || name !== 'payload') &&
        (!scalarAuthor || (!name.startsWith('author') && name !== 'schemaVersion')),
    )
    .map(([name, value]) => customFieldHidden(name, name === 'payload' ? preparedPayload : value))
    .join('');
  const advanced = `<label for="recover-custom-field-payload">${escape(text.advanced)}</label><p>${escape(text.advanced_hint)}</p><textarea id="recover-custom-field-payload" name="payload" rows="20" maxlength="4096">${escape(fields.get('payload') ?? '')}</textarea>`;
  const form = editable
    ? `<form action="${escape(page.action)}" method="post">${proof}${controls ?? advanced}<button type="submit">${escape(memberActions.includes(page.action) ? text.save : text.save_draft)}</button></form>`
    : '';
  const retained =
    memberActions.includes(page.action) && controls === null
      ? `<section aria-labelledby="custom-field-submitted-inputs"><h2 id="custom-field-submitted-inputs">${escape(text.submitted_inputs)}</h2><p>${escape(text.recovery_unavailable)}</p>${submittedInputs.length > 0 ? '<ol>' + submittedInputs.map((value) => '<li>' + escape(value) + '</li>').join('') + '</ol>' : ''}</section>`
      : '';
  const destination = isNativeReturnPath(page.destination) ? page.destination : '/portal/profile';
  return `<p role="alert">${escape(copy('mutation.custom_field_refused'))}</p>${form}${retained}<p><a href="${escape(destination)}">${escape(copy('mutation.lookup'))}</a></p>${memberActions.includes(page.action) && controls !== null ? '<p>' + escape(text.no_js_answers_hint) + '</p>' : ''}<p>${escape(text.input_budget)}</p><p>${escape(text.value_budget)}</p><p>${escape(site.displayName)}</p>`;
}
