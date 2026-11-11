'use client';

import { customFieldTextMode, decodeCustomFieldText, encodeCustomFieldText } from '@learnstack/sdk';
import type { CustomFieldTextMode } from '@learnstack/sdk';
import { useEffect, useRef, useState } from 'react';

import type { FieldCopy } from './custom-field-controls';

type Props = Readonly<{
  id: string;
  name?: string;
  modeName?: string;
  value: string;
  maxLength?: number;
  maxBytes?: number;
  required?: boolean;
  lang?: string;
  copy: FieldCopy;
  onChange?: (value: string) => void;
  onValidity?: (id: string, valid: boolean) => void;
}>;

/** One lossless authoring control for literals, labels, and notices. */
export function CustomFieldTextEditor({
  id,
  name,
  modeName,
  value,
  maxLength,
  maxBytes,
  required,
  lang,
  copy,
  onChange,
  onValidity,
}: Props) {
  const initialMode = customFieldTextMode(value);
  const [mode, setMode] = useState<CustomFieldTextMode>(initialMode);
  const [source, setSource] = useState(() => encodeCustomFieldText(value, initialMode));
  const control = useRef<HTMLTextAreaElement>(null);
  const acceptedValue = useRef(value);
  useEffect(() => {
    if (acceptedValue.current === value) return;
    acceptedValue.current = value;
    const nextMode = customFieldTextMode(value);
    setMode(nextMode);
    setSource(encodeCustomFieldText(value, nextMode));
  }, [value]);
  let decoded: string | null = null;
  try {
    decoded = decodeCustomFieldText(source, mode);
  } catch {
    /* Preserve malformed source for correction. */
  }
  const valid =
    decoded !== null &&
    (!required || decoded.length > 0) &&
    (maxLength === undefined || [...decoded].length <= maxLength) &&
    (maxBytes === undefined || new TextEncoder().encode(decoded).length <= maxBytes);
  useEffect(() => {
    control.current?.setCustomValidity(valid ? '' : copy.errors);
    onValidity?.(id, valid);
  }, [id, valid, onValidity, copy.errors]);
  useEffect(() => () => onValidity?.(id, true), [id, onValidity]);
  function changeSource(next: string, nextMode = mode) {
    setSource(next);
    try {
      const text = decodeCustomFieldText(next, nextMode);
      const accepted =
        (!required || text.length > 0) &&
        (maxLength === undefined || [...text].length <= maxLength) &&
        (maxBytes === undefined || new TextEncoder().encode(text).length <= maxBytes);
      control.current?.setCustomValidity(accepted ? '' : copy.errors);
      onValidity?.(id, accepted);
      if (accepted) {
        acceptedValue.current = text;
        onChange?.(text);
      }
    } catch {
      control.current?.setCustomValidity(copy.errors);
      onValidity?.(id, false);
    }
  }
  return (
    <>
      <label htmlFor={id + '-textmode'}>{copy.text_entry_mode}</label>
      <select
        id={id + '-textmode'}
        name={modeName}
        value={mode}
        onChange={(event) => {
          const next = event.target.value;
          if (
            (next !== 'plain' && next !== 'escaped') ||
            decoded === null ||
            (next === 'plain' && decoded.includes('\r'))
          )
            return;
          setMode(next);
          changeSource(encodeCustomFieldText(decoded, next), next);
        }}
        aria-describedby={id + '-text-hint'}
      >
        <option value="plain" disabled={decoded === null || decoded.includes('\r')}>
          {copy.text_plain}
        </option>
        <option value="escaped">{copy.text_exact}</option>
      </select>
      <textarea
        id={id}
        name={name}
        ref={control}
        value={source}
        rows={3}
        lang={lang}
        maxLength={(maxLength ?? maxBytes ?? 512) * 2}
        required={required}
        onChange={(event) => changeSource(event.target.value)}
        aria-invalid={!valid || undefined}
        aria-describedby={id + '-text-hint'}
      />
      <p id={id + '-text-hint'}>{copy.author_text_hint}</p>
    </>
  );
}
