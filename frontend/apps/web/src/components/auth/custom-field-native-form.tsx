'use client';

import {
  parseCustomFieldNumber,
  decodeCustomFieldChoice,
  decodeCustomFieldText,
} from '@learnstack/sdk';
import type { CustomFieldPatch, JsonValue } from '@learnstack/sdk/administration-server';
import { useEffect, useRef, useState } from 'react';
import type { FormEvent, ReactNode } from 'react';

import type { FieldCopy } from './custom-field-controls';
import { fieldPropertyKeys } from './custom-field-schema';
import type { FieldEnvelopeSchema, FieldSchema } from './custom-field-schema';

const answerName = /^(?:answer|presence|count|textmode)_/;

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
    const value = schema.enum
      ? decodeCustomFieldChoice(raw, schema.enum)
      : decodeCustomFieldText(raw, text('textmode_' + path));
    if ([...value].length < schema.minLength || [...value].length > schema.maxLength)
      throw new Error('Invalid text answer');
    return value;
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
  const formRef = useRef<HTMLFormElement>(null);
  const preparedPayload = useRef<string | null>(null);
  const [enhanced, setEnhanced] = useState(false);
  const [invalid, setInvalid] = useState(false);
  useEffect(() => {
    const form = formRef.current;
    if (!form) return;
    const serialize = (event: Event) => {
      const payload = preparedPayload.current;
      if (payload === null || !('formData' in event)) return;
      const data = (event as FormDataEvent).formData;
      for (const name of [...data.keys()]) if (answerName.test(name)) data.delete(name);
      data.set('payload', payload);
      preparedPayload.current = null;
    };
    form.addEventListener('formdata', serialize);
    setEnhanced(true);
    return () => form.removeEventListener('formdata', serialize);
  }, []);

  function submit(event: FormEvent<HTMLFormElement>) {
    const form = event.currentTarget;
    try {
      preparedPayload.current = null;
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
      preparedPayload.current = payload;
    } catch {
      preparedPayload.current = null;
      event.preventDefault();
      setInvalid(true);
    }
  }
  return (
    <form
      ref={formRef}
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
