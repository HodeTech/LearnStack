import type { ReactNode } from 'react';

import type enAdmin from '@/i18n/messages/en/admin.json';

import { InteractiveFieldControls } from './custom-field-control-node';
import { authoredText, fieldPropertyKeys } from './custom-field-schema';
import type { ControlLabels, FieldSchema } from './custom-field-schema';

export type FieldCopy = typeof enAdmin.custom_fields;
export type FieldControlsProps = Readonly<{
  schema: FieldSchema;
  labels: Readonly<Record<string, string>>;
  formLabels?: readonly ControlLabels[] | null;
  locale: string;
  defaultLocale: string;
  copy: FieldCopy;
  prefix: string;
  value?: unknown;
  required?: boolean;
  submitted?: Readonly<Record<string, string>>;
  staticControls?: boolean;
  idPrefix?: string;
}>;

/** The client owns actual presence/count controls; numeric input remains an unparsed lexeme. */
export function FieldControls(props: FieldControlsProps) {
  return <InteractiveFieldControls {...props} />;
}

/** Same finite controls with all potential branches available for native no-script submission. */
export function StaticFieldControls(props: FieldControlsProps) {
  return <InteractiveFieldControls {...props} staticControls idPrefix={'ns-' + props.prefix} />;
}

/** Values appear only in their authorized projection; withheld content is never traversed. */
export function FieldAnswer({
  schema,
  value,
  formLabels,
  locale,
  defaultLocale,
  copy,
}: Omit<FieldControlsProps, 'prefix' | 'required'>) {
  function answer(node: FieldSchema, current: unknown, location: string): ReactNode {
    if (current === undefined) return copy.missing;
    if (
      node.type === 'object' &&
      current !== null &&
      typeof current === 'object' &&
      !Array.isArray(current)
    )
      return (
        <dl className="auth-values">
          {fieldPropertyKeys(node).map((key, index) => {
            if (!Object.hasOwn(current, key)) return null;
            const next = location + (location === '/' ? '' : '/') + index;
            const text = authoredText(
              formLabels?.find((entry) => entry.location === next)?.labels ?? {},
              locale,
              defaultLocale,
            );
            return (
              <div key={key}>
                <dt lang={text.lang}>{text.text}</dt>
                <dd>
                  {answer(node.properties[key]!, (current as Record<string, unknown>)[key], next)}
                </dd>
              </div>
            );
          })}
        </dl>
      );
    if (node.type === 'array' && Array.isArray(current))
      return (
        <ol>
          {current.map((item, index) => (
            <li key={index}>
              {answer(node.items, item, location + (location === '/' ? '' : '/') + '0')}
            </li>
          ))}
        </ol>
      );
    if (node.type === 'boolean') return current === true ? copy.yes : copy.no;
    if (node.type === 'string' && node.enum) {
      const index = node.enum.indexOf(String(current));
      const text = authoredText(
        formLabels?.find((entry) => entry.location === location)?.choices?.[index] ?? {},
        locale,
        defaultLocale,
      );
      return <span lang={text.lang}>{text.text}</span>;
    }
    return typeof current === 'string' || typeof current === 'number'
      ? String(current)
      : copy.unsupported;
  }
  return (
    <div className="custom-field-answer" lang={locale}>
      {answer(schema, value, '/')}
    </div>
  );
}
