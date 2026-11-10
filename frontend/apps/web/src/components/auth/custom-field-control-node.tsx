'use client';

import { useState } from 'react';

import type { FieldControlsProps } from './custom-field-controls';
import { authoredText, fieldPropertyKeys } from './custom-field-schema';
import type { FieldSchema } from './custom-field-schema';

type ControlNodeProps = Omit<FieldControlsProps, 'schema' | 'value' | 'required'> &
  Readonly<{
    node: FieldSchema;
    location: string;
    path: string;
    current: unknown;
    mandatory: boolean;
    itemLabel?: string;
  }>;

export function InteractiveFieldControls({
  schema,
  value,
  required = true,
  ...props
}: FieldControlsProps) {
  return (
    <ControlNode
      key={JSON.stringify(schema)}
      {...props}
      node={schema}
      location="/"
      path="root"
      current={value}
      mandatory={required}
    />
  );
}

/** Native controls preserve numeric lexemes. Requiredness is validated on the explicit save only. */
function ControlNode({
  node,
  location,
  path,
  current,
  mandatory,
  itemLabel,
  labels,
  formLabels,
  locale,
  defaultLocale,
  copy,
  prefix,
  submitted,
  staticControls,
  idPrefix,
}: ControlNodeProps) {
  const metadata = formLabels?.find((entry) => entry.location === location);
  const label = authoredText(
    location === '/' ? labels : (metadata?.labels ?? {}),
    locale,
    defaultLocale,
  );
  const id = (idPrefix ?? prefix) + '-' + path;
  const name = prefix + '_' + path;
  const hint = id + '-hint';
  const title = (
    <>
      <span lang={label.lang}>{label.text}</span>
      {itemLabel && <> · {itemLabel}</>}
      {mandatory && <> · {copy.required}</>}
    </>
  );
  const [present, setPresent] = useState(
    mandatory ||
      (submitted?.['presence_' + name] ?? (current === undefined ? 'off' : 'on')) === 'on',
  );
  const items = Array.isArray(current) ? current : [];
  const minimumCount = node.type === 'array' ? node.minItems : 0;
  const maximumCount = node.type === 'array' ? node.maxItems : 0;
  const postedCount = submitted?.['count_' + name];
  const initialCount =
    postedCount && /^[0-9]+$/.test(postedCount)
      ? Number(postedCount)
      : Math.max(minimumCount, items.length);
  const [count, setCount] = useState(Math.min(maximumCount, Math.max(minimumCount, initialCount)));
  const optional = !mandatory && (
    <div className="auth-field">
      <label htmlFor={id + '-present'}>{copy.include}</label>
      <select
        id={id + '-present'}
        name={'presence_' + name}
        value={present ? 'on' : 'off'}
        onChange={(event) => setPresent(event.target.value === 'on')}
      >
        <option value="off">{copy.no}</option>
        <option value="on">{copy.yes}</option>
      </select>
    </div>
  );
  if (!present && !staticControls)
    return (
      <fieldset className="custom-field-group">
        <legend>{title}</legend>
        {optional}
      </fieldset>
    );
  const childLocation = (index: number) => location + (location === '/' ? '' : '/') + index;
  if (node.type === 'object') {
    const object =
      typeof current === 'object' && current !== null && !Array.isArray(current)
        ? (current as Readonly<Record<string, unknown>>)
        : {};
    return (
      <fieldset key={id} className="custom-field-group">
        <legend>{title}</legend>
        {optional}
        {fieldPropertyKeys(node).map((key, index) => (
          <ControlNode
            key={key}
            node={node.properties[key]!}
            location={childLocation(index)}
            path={path + '_' + index}
            current={object[key]}
            mandatory={node.required.includes(key)}
            labels={labels}
            formLabels={formLabels}
            locale={locale}
            defaultLocale={defaultLocale}
            copy={copy}
            prefix={prefix}
            submitted={submitted}
            staticControls={staticControls}
            idPrefix={idPrefix}
          />
        ))}
      </fieldset>
    );
  }
  if (node.type === 'array') {
    return (
      <fieldset key={id} className="custom-field-group">
        <legend>{title}</legend>
        {optional}
        <label htmlFor={id + '-count'}>{copy.count}</label>
        <select
          id={id + '-count'}
          name={'count_' + name}
          value={String(count)}
          onChange={(event) => setCount(Number(event.target.value))}
          aria-describedby={hint}
        >
          {Array.from(
            { length: node.maxItems - node.minItems + 1 },
            (_, index) => node.minItems + index,
          ).map((length) => (
            <option key={length} value={length}>
              {length}
            </option>
          ))}
        </select>
        <p id={hint} aria-live="polite">
          {copy.count}: {count} · {copy.min}: {node.minItems} · {copy.max}: {node.maxItems}
        </p>
        {Array.from({ length: staticControls ? node.maxItems : count }, (_, index) => (
          <details key={index} open={index < count}>
            <summary>
              {copy.item} {index + 1}
            </summary>
            <ControlNode
              node={node.items}
              location={childLocation(0)}
              path={path + '_' + index}
              current={items[index]}
              mandatory
              labels={labels}
              formLabels={formLabels}
              locale={locale}
              defaultLocale={defaultLocale}
              copy={copy}
              prefix={prefix}
              submitted={submitted}
              staticControls={staticControls}
              idPrefix={idPrefix}
              itemLabel={copy.item + ' ' + (index + 1)}
            />
          </details>
        ))}
      </fieldset>
    );
  }
  return (
    <div className="auth-field" key={id}>
      {optional}
      <label htmlFor={id}>{title}</label>
      {node.type === 'boolean' ? (
        <select
          id={id}
          name={'answer_' + name}
          defaultValue={
            submitted?.['answer_' + name] ?? (typeof current === 'boolean' ? String(current) : '')
          }
          aria-describedby={hint}
          aria-required={mandatory}
        >
          <option value="">{copy.choose}</option>
          <option value="true">{copy.yes}</option>
          <option value="false">{copy.no}</option>
        </select>
      ) : node.type === 'string' && node.enum ? (
        <select
          id={id}
          name={'answer_' + name}
          defaultValue={
            submitted?.['answer_' + name] ?? (typeof current === 'string' ? current : '')
          }
          aria-required={mandatory}
          aria-describedby={hint}
        >
          <option value="">{copy.choose}</option>
          {node.enum.map((choice, index) => {
            const text = authoredText(metadata?.choices?.[index] ?? {}, locale, defaultLocale);
            return (
              <option key={choice} value={choice} lang={text.lang}>
                {text.text}
              </option>
            );
          })}
        </select>
      ) : node.type === 'string' ? (
        <input
          id={id}
          name={'answer_' + name}
          defaultValue={
            submitted?.['answer_' + name] ?? (typeof current === 'string' ? current : '')
          }
          maxLength={node.maxLength * 2}
          aria-required={mandatory}
          aria-describedby={hint}
        />
      ) : (
        <input
          id={id}
          name={'answer_' + name}
          type="text"
          inputMode="decimal"
          defaultValue={
            submitted?.['answer_' + name] ?? (typeof current === 'number' ? String(current) : '')
          }
          maxLength={128}
          aria-required={mandatory}
          aria-describedby={hint}
        />
      )}
      <p id={hint}>
        {node.type === 'string' ? (
          <>
            {copy.min}: {node.minLength} · {copy.max}: {node.maxLength}
          </>
        ) : node.type === 'boolean' ? (
          copy.choose
        ) : (
          <>
            {copy.min}: {node.minimum} · {copy.max}: {node.maximum}. {copy.numeric_hint}
          </>
        )}
      </p>
    </div>
  );
}
