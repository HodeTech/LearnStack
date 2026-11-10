'use client';

import { parseCustomFieldNumber } from '@learnstack/sdk';
import type { CustomFieldPatch, JsonValue } from '@learnstack/sdk/administration-server';
import { useEffect, useState } from 'react';
import type { FormEvent, ReactNode } from 'react';

import type { FieldCopy } from './custom-field-controls';
import { fieldPropertyKeys } from './custom-field-schema';
import type { FieldEnvelopeSchema, FieldSchema } from './custom-field-schema';

const answerName = /^(?:answer|presence|count)_/;

function readAnswer(
  schema: FieldSchema,
  fields: FormData,
  path: string,
  required: boolean,
): JsonValue | undefined {
  const text = (name: string) => {
    const value = fields.get(name);
    if (typeof value !== 'string') throw new Error('Incomplete answer');
    return value;
  };
  if (!required && text('presence_' + path) === 'off') return undefined;
  if (schema.type === 'object')
    return Object.fromEntries(
      fieldPropertyKeys(schema).flatMap((key, index) => {
        const value = readAnswer(
          schema.properties[key]!,
          fields,
          path + '_' + index,
          schema.required.includes(key),
        );
        return value === undefined ? [] : [[key, value]];
      }),
    );
  if (schema.type === 'array') {
    const count = parseCustomFieldNumber(text('count_' + path), true);
    if (count < schema.minItems || count > schema.maxItems) throw new Error('Invalid item count');
    return Array.from(
      { length: count },
      (_, index) => readAnswer(schema.items, fields, path + '_' + index, true) as JsonValue,
    );
  }
  const raw = text('answer_' + path);
  if (schema.type === 'string') {
    if (
      [...raw].length < schema.minLength ||
      [...raw].length > schema.maxLength ||
      (schema.enum && !schema.enum.includes(raw))
    )
      throw new Error('Invalid text answer');
    return raw;
  }
  if (schema.type === 'boolean') {
    if (raw !== 'true' && raw !== 'false') throw new Error('Invalid choice');
    return raw === 'true';
  }
  const value = parseCustomFieldNumber(raw, schema.type === 'integer');
  if (value < schema.minimum || value > schema.maximum) throw new Error('Invalid numeric answer');
  return value;
}

type CustomFieldNativeFormProps = Readonly<{
  action: string;
  patch: CustomFieldPatch;
  schemata: readonly FieldEnvelopeSchema[];
  complex: boolean;
  copy: FieldCopy;
  children: ReactNode;
}>;

/** Keep native navigation while submitting only the canonical answer, not hundreds of control names. */
export function CustomFieldNativeForm({
  action,
  patch,
  schemata,
  complex,
  copy,
  children,
}: CustomFieldNativeFormProps) {
  const [enhanced, setEnhanced] = useState(false);
  const [invalid, setInvalid] = useState(false);
  useEffect(() => {
    setEnhanced(true);
  }, []);
  function submit(event: FormEvent<HTMLFormElement>) {
    const form = event.currentTarget;
    try {
      const fields = new FormData(form);
      const complete = {
        ...patch,
        set: patch.set.map((operation, index) => ({
          ...operation,
          envelope: {
            value: readAnswer(schemata[index]!.properties.value, fields, index + '_root', true),
          },
        })),
      };
      const payload = JSON.stringify(complete);
      if (new TextEncoder().encode(payload).length > 4096)
        throw new Error('Answer exceeds its limit');
      const encoded = new URLSearchParams();
      for (const [name, value] of fields) {
        if (typeof value !== 'string') throw new Error('Unsupported answer');
        if (!answerName.test(name) && name !== 'payload') encoded.append(name, value);
      }
      encoded.set('payload', payload);
      if (new TextEncoder().encode(encoded.toString()).length > 16384)
        throw new Error('Request exceeds its limit');
      const input = form.querySelector<HTMLInputElement>('input[name="payload"]');
      if (!input) throw new Error('Prepared form unavailable');
      input.value = payload;
      for (const control of form.querySelectorAll<HTMLInputElement | HTMLSelectElement>('[name]'))
        if (answerName.test(control.name)) control.removeAttribute('name');
    } catch {
      event.preventDefault();
      setInvalid(true);
    }
  }
  return (
    <form
      action={action}
      method="post"
      className="custom-field-editor"
      hidden={complex && !enhanced}
      onSubmit={submit}
    >
      {children}
      {invalid && (
        <p role="alert">
          {copy.errors} {copy.value_budget}
        </p>
      )}
    </form>
  );
}
