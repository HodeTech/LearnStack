import {
  customFieldChoiceToken,
  customFieldTextMode,
  decodeCustomFieldText,
  encodeCustomFieldText,
  normalizeCustomFieldEditorLines,
} from '@learnstack/sdk';

import type { FieldControlsProps, FieldCopy } from './custom-field-controls';
import {
  authoredText,
  envelopeSchema,
  fieldPropertyKeys,
  readFieldSchema,
} from './custom-field-schema';
import type { FieldSchema } from './custom-field-schema';

/** Literal text and attribute values share one escaping boundary in native form documents. */
export const escapeCustomFieldHtml = (value: string): string =>
  value.replace(
    /[&<>"']/g,
    (character) =>
      ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]!,
  );
export const customFieldHidden = (name: string, value: string): string =>
  `<input type="hidden" name="${escapeCustomFieldHtml(name)}" value="${escapeCustomFieldHtml(value)}">`;
export function customFieldOptions(
  choices: readonly Readonly<{ value: string; label: string; lang?: string; disabled?: boolean }>[],
  selected: string,
): string {
  return choices
    .map(
      (choice) =>
        `<option value="${escapeCustomFieldHtml(choice.value)}"${choice.lang === undefined ? '' : ` lang="${escapeCustomFieldHtml(choice.lang)}"`}${choice.disabled ? ' disabled' : ''}${choice.value === selected ? ' selected' : ''}>${escapeCustomFieldHtml(choice.label)}</option>`,
    )
    .join('');
}

export function customFieldTextareaContent(source: string): string {
  const normalized = normalizeCustomFieldEditorLines(source);
  return (normalized.startsWith('\n') ? '\n' : '') + escapeCustomFieldHtml(normalized);
}

/** Validation recovery keeps the submitted representation, including malformed text for correction. */
export function renderCustomFieldAuthorText(
  props: Readonly<{
    id: string;
    name: string;
    modeName: string;
    source: string;
    mode: string;
    maxBytes: number;
    copy: FieldCopy;
  }>,
): string {
  const { id, name, modeName, source, mode, maxBytes, copy } = props;
  const escape = escapeCustomFieldHtml;
  let allowsPlain = false;
  try {
    allowsPlain = !decodeCustomFieldText(source, mode).includes('\r');
  } catch {
    /* Invalid text stays editable in its original representation. */
  }
  return `<label for="${escape(id + '-textmode')}">${escape(copy.text_entry_mode)}</label><select id="${escape(id + '-textmode')}" name="${escape(modeName)}" aria-describedby="${escape(id + '-text-hint')}">${customFieldOptions(
    [
      ...(['plain', 'escaped'].includes(mode) ? [] : [{ value: '', label: copy.choose }]),
      { value: 'plain', label: copy.text_plain, disabled: !allowsPlain },
      { value: 'escaped', label: copy.text_exact },
    ],
    ['plain', 'escaped'].includes(mode) ? mode : '',
  )}</select><textarea id="${escape(id)}" name="${escape(name)}" rows="3" maxlength="${maxBytes * 2}" aria-describedby="${escape(id + '-text-hint')}">${customFieldTextareaContent(source)}</textarea><p id="${escape(id + '-text-hint')}">${escape(copy.author_text_hint)}</p>`;
}

/** Bounded native HTML for the same finite controls, without a React server renderer dependency. */
export function renderStaticCustomFieldControls(props: FieldControlsProps): string {
  const admitted = readFieldSchema(envelopeSchema(props.schema));
  if (!admitted) throw new Error('Prepared form unavailable');
  const { labels, formLabels, locale, defaultLocale, copy, prefix, submitted } = props;
  const escape = escapeCustomFieldHtml;
  function control(
    node: FieldSchema,
    location: string,
    path: string,
    current: unknown,
    mandatory: boolean,
    itemLabel?: string,
  ): string {
    const metadata = formLabels?.find((entry) => entry.location === location);
    const label = authoredText(
      location === '/' ? labels : (metadata?.labels ?? {}),
      locale,
      defaultLocale,
    );
    const id = (props.idPrefix ?? 'ns-' + prefix) + '-' + path;
    const name = prefix + '_' + path;
    const hint = id + '-hint';
    const title = `<span lang="${escape(label.lang)}">${escape(label.text)}</span>${itemLabel ? ' · ' + escape(itemLabel) : ''}${mandatory ? ' · ' + escape(copy.required) : ''}`;
    const present =
      mandatory ||
      (submitted?.['presence_' + name] ?? (current === undefined ? 'off' : 'on')) === 'on';
    const optional = mandatory
      ? ''
      : `<div class="auth-field"><label id="${escape(id + '-present-label')}" for="${escape(id + '-present')}">${escape(copy.include)}</label><select id="${escape(id + '-present')}" aria-labelledby="${escape(id + '-title ' + id + '-present-label')}" name="${escape('presence_' + name)}">${customFieldOptions(
          [
            { value: 'off', label: copy.no },
            { value: 'on', label: copy.yes },
          ],
          present ? 'on' : 'off',
        )}</select></div>`;
    const childLocation = (index: number) => location + (location === '/' ? '' : '/') + index;
    if (node.type === 'object') {
      const object =
        typeof current === 'object' && current !== null && !Array.isArray(current)
          ? (current as Readonly<Record<string, unknown>>)
          : {};
      return `<fieldset class="custom-field-group"><legend id="${escape(id + '-title')}">${title}</legend>${optional}${fieldPropertyKeys(
        node,
      )
        .map((key, index) =>
          control(
            node.properties[key]!,
            childLocation(index),
            path + '_' + index,
            object[key],
            node.required.includes(key),
          ),
        )
        .join('')}</fieldset>`;
    }
    if (node.type === 'array') {
      const items = Array.isArray(current) ? current : [];
      const rawCount = submitted?.['count_' + name];
      const initial =
        rawCount && /^[0-9]+$/.test(rawCount)
          ? Number(rawCount)
          : Math.max(node.minItems, items.length);
      const count = Math.min(node.maxItems, Math.max(node.minItems, initial));
      return `<fieldset class="custom-field-group"><legend id="${escape(id + '-title')}">${title}</legend>${optional}<label for="${escape(id + '-count')}">${escape(copy.count)}</label><select id="${escape(id + '-count')}" name="${escape('count_' + name)}" aria-describedby="${escape(hint)}">${customFieldOptions(
        Array.from({ length: node.maxItems - node.minItems + 1 }, (_, index) => ({
          value: String(node.minItems + index),
          label: String(node.minItems + index),
        })),
        String(count),
      )}</select><p id="${escape(hint)}" aria-live="polite">${escape(copy.count)}: ${count} · ${escape(copy.min)}: ${node.minItems} · ${escape(copy.max)}: ${node.maxItems}</p>${Array.from({ length: node.maxItems }, (_, index) => `<details${index < count ? ' open' : ''}><summary>${escape(copy.item)} ${index + 1}</summary>${control(node.items, childLocation(0), path + '_' + index, items[index], true, copy.item + ' ' + (index + 1))}</details>`).join('')}</fieldset>`;
    }
    const common = `id="${escape(id)}" name="${escape('answer_' + name)}" aria-required="${mandatory}" aria-describedby="${escape(hint)}"`;
    let input: string;
    if (node.type === 'boolean') {
      const value =
        submitted?.['answer_' + name] ?? (typeof current === 'boolean' ? String(current) : '');
      input = `<select ${common}>${customFieldOptions(
        [
          { value: '', label: copy.choose },
          { value: 'true', label: copy.yes },
          { value: 'false', label: copy.no },
        ],
        value,
      )}</select>`;
    } else if (node.type === 'string' && node.enum) {
      const value =
        submitted?.['answer_' + name] ??
        (typeof current === 'string' && node.enum.includes(current)
          ? customFieldChoiceToken(node.enum.indexOf(current))
          : '');
      input = `<select ${common}>${customFieldOptions(
        [
          { value: '', label: copy.choose },
          ...node.enum.map((_, index) => {
            const text = authoredText(metadata?.choices?.[index] ?? {}, locale, defaultLocale);
            return { value: customFieldChoiceToken(index), label: text.text, lang: text.lang };
          }),
        ],
        value,
      )}</select>`;
    } else if (node.type === 'string') {
      const stored = typeof current === 'string' ? current : '';
      const requested = submitted?.['textmode_' + name];
      const mode =
        requested === 'plain' || requested === 'escaped' ? requested : customFieldTextMode(stored);
      const source =
        submitted?.['answer_' + name] === undefined
          ? encodeCustomFieldText(stored, mode)
          : normalizeCustomFieldEditorLines(submitted['answer_' + name]!);
      let allowsPlain = false;
      try {
        allowsPlain = !decodeCustomFieldText(source, mode).includes('\r');
      } catch {
        /* Exact text remains editable without a lossy mode change. */
      }
      input = `<label for="${escape(id + '-textmode')}">${escape(copy.text_entry_mode)}</label><select id="${escape(id + '-textmode')}" name="${escape('textmode_' + name)}" aria-describedby="${escape(hint)}">${customFieldOptions(
        [
          { value: 'plain', label: copy.text_plain, disabled: !allowsPlain },
          { value: 'escaped', label: copy.text_exact },
        ],
        mode,
      )}</select><textarea ${common} maxlength="${node.maxLength * 2}" rows="3">${customFieldTextareaContent(source)}</textarea>`;
    } else {
      const value =
        submitted?.['answer_' + name] ?? (typeof current === 'number' ? String(current) : '');
      input = `<input ${common} type="text" inputmode="decimal" maxlength="128" value="${escape(value)}">`;
    }

    const help =
      node.type === 'string'
        ? `${escape(copy.min)}: ${node.minLength} · ${escape(copy.max)}: ${node.maxLength}.${node.enum ? '' : ' ' + escape(copy.text_entry_hint)}`
        : node.type === 'boolean'
          ? escape(copy.choose)
          : `${escape(copy.min)}: ${node.minimum} · ${escape(copy.max)}: ${node.maximum}. ${escape(copy.numeric_hint)}`;
    return `<div class="auth-field"><label id="${escape(id + '-title')}" for="${escape(id)}">${title}</label>${optional}${input}<p id="${escape(hint)}">${help}</p></div>`;
  }
  return control(admitted.properties.value, '/', 'root', props.value, props.required ?? true);
}
