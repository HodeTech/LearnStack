import type { FieldControlsProps } from './custom-field-controls';
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
  choices: readonly Readonly<{ value: string; label: string; lang?: string }>[],
  selected: string,
): string {
  return choices
    .map(
      (choice) =>
        `<option value="${escapeCustomFieldHtml(choice.value)}"${choice.lang === undefined ? '' : ` lang="${escapeCustomFieldHtml(choice.lang)}"`}${choice.value === selected ? ' selected' : ''}>${escapeCustomFieldHtml(choice.label)}</option>`,
    )
    .join('');
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
      : `<div class="auth-field"><label for="${escape(id + '-present')}">${escape(copy.include)}</label><select id="${escape(id + '-present')}" name="${escape('presence_' + name)}">${customFieldOptions(
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
      return `<fieldset class="custom-field-group"><legend>${title}</legend>${optional}${fieldPropertyKeys(
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
      return `<fieldset class="custom-field-group"><legend>${title}</legend>${optional}<label for="${escape(id + '-count')}">${escape(copy.count)}</label><select id="${escape(id + '-count')}" name="${escape('count_' + name)}" aria-describedby="${escape(hint)}">${customFieldOptions(
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
      const value = submitted?.['answer_' + name] ?? (typeof current === 'string' ? current : '');
      input = `<select ${common}>${customFieldOptions(
        [
          { value: '', label: copy.choose },
          ...node.enum.map((value, index) => {
            const text = authoredText(metadata?.choices?.[index] ?? {}, locale, defaultLocale);
            return { value, label: text.text, lang: text.lang };
          }),
        ],
        value,
      )}</select>`;
    } else {
      const value =
        submitted?.['answer_' + name] ??
        (node.type === 'string'
          ? typeof current === 'string'
            ? current
            : ''
          : typeof current === 'number'
            ? String(current)
            : '');
      input = `<input ${common}${node.type === 'string' ? '' : ' type="text" inputmode="decimal"'} maxlength="${node.type === 'string' ? node.maxLength * 2 : 128}" value="${escape(value)}">`;
    }
    const help =
      node.type === 'string'
        ? `${escape(copy.min)}: ${node.minLength} · ${escape(copy.max)}: ${node.maxLength}`
        : node.type === 'boolean'
          ? escape(copy.choose)
          : `${escape(copy.min)}: ${node.minimum} · ${escape(copy.max)}: ${node.maximum}. ${escape(copy.numeric_hint)}`;
    return `<div class="auth-field">${optional}<label for="${escape(id)}">${title}</label>${input}<p id="${escape(hint)}">${help}</p></div>`;
  }
  return control(admitted.properties.value, '/', 'root', props.value, props.required ?? true);
}
