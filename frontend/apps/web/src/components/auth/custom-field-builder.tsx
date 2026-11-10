'use client';

import { parseCustomFieldNumber } from '@learnstack/sdk';
import type {
  CustomFieldDefinition,
  CustomFieldInput,
} from '@learnstack/sdk/administration-server';
import { useCallback, useEffect, useState } from 'react';
import type { FormEvent } from 'react';

import { FieldControls } from './custom-field-controls';
import type { FieldCopy } from './custom-field-controls';
import {
  envelopeSchema,
  fieldPropertyKeys,
  readFieldSchema,
  validFieldKey,
  hasDisabledFieldTranslations,
} from './custom-field-schema';
import type { ControlLabels, FieldSchema } from './custom-field-schema';
import { CustomFieldTextEditor } from './custom-field-text-editor';

type DraftNode = {
  type: FieldSchema['type'] | 'enum';
  labels: Record<string, string>;
  minimum: string;
  maximum: string;
  choices: { value: string; labels: Record<string, string> }[];
  children: { key: string; required: boolean; node: DraftNode }[];
  item?: DraftNode;
};
const fresh = (): DraftNode => ({
  type: 'string',
  labels: {},
  minimum: '1',
  maximum: '32',
  choices: [{ value: '', labels: {} }],
  children: [],
});
function draftFrom(
  schema: FieldSchema,
  labels: Readonly<Record<string, string>>,
  metadata: readonly ControlLabels[] | null,
  location = '/',
): DraftNode {
  const entry = metadata?.find((item) => item.location === location);
  const childLocation = (index: number) => location + (location === '/' ? '' : '/') + index;
  const node = {
    ...fresh(),
    type: schema.type,
    labels: { ...(location === '/' ? labels : (entry?.labels ?? {})) },
  };
  if (schema.type === 'string')
    return {
      ...node,
      type: schema.enum ? 'enum' : 'string',
      minimum: String(schema.minLength),
      maximum: String(schema.maxLength),
      choices:
        schema.enum?.map((value, index) => ({ value, labels: { ...entry?.choices?.[index] } })) ??
        node.choices,
    };
  if (schema.type === 'integer' || schema.type === 'number')
    return { ...node, minimum: String(schema.minimum), maximum: String(schema.maximum) };
  if (schema.type === 'object')
    return {
      ...node,
      children: fieldPropertyKeys(schema).map((key, index) => ({
        key,
        required: schema.required.includes(key),
        node: draftFrom(schema.properties[key]!, labels, metadata, childLocation(index)),
      })),
    };
  if (schema.type === 'array')
    return {
      ...node,
      minimum: String(schema.minItems),
      maximum: String(schema.maxItems),
      item: draftFrom(schema.items, labels, metadata, childLocation(0)),
    };
  return node;
}
const localized = (values: Readonly<Record<string, string>>, locales: readonly string[]) =>
  Object.fromEntries(
    Object.entries(values).filter(
      ([locale, value]) => locales.includes(locale) && value.length > 0,
    ),
  );
const numeric = (text: string, integer = true) => parseCustomFieldNumber(text, integer);

function compile(
  node: DraftNode,
  location: string,
  metadata: ControlLabels[],
  locales: readonly string[],
): FieldSchema {
  const labels = localized(node.labels, locales);
  if (location !== '/' || node.type === 'enum')
    metadata.push({
      location,
      labels: location === '/' ? null : labels,
      choices:
        node.type === 'enum'
          ? node.choices.map((choice) => localized(choice.labels, locales))
          : null,
    });
  const childLocation = (index: number) => location + (location === '/' ? '' : '/') + index;
  switch (node.type) {
    case 'boolean':
      return { type: 'boolean' };
    case 'string':
    case 'enum':
      return {
        type: 'string',
        minLength: numeric(node.minimum),
        maxLength: numeric(node.maximum),
        ...(node.type === 'enum' ? { enum: node.choices.map((choice) => choice.value) } : {}),
      };
    case 'integer':
    case 'number':
      return {
        type: node.type,
        minimum: numeric(node.minimum, node.type === 'integer'),
        maximum: numeric(node.maximum, node.type === 'integer'),
      };
    case 'array':
      return {
        type: 'array',
        minItems: numeric(node.minimum),
        maxItems: numeric(node.maximum),
        items: compile(node.item ?? fresh(), childLocation(0), metadata, locales),
      };
    case 'object': {
      const children = [...node.children].sort((a, b) =>
        a.key < b.key ? -1 : a.key > b.key ? 1 : 0,
      );
      if (new Set(children.map((child) => child.key)).size !== children.length)
        throw new Error('Invalid structure');
      return {
        type: 'object',
        properties: Object.fromEntries(
          children.map((child, index) => [
            child.key,
            compile(child.node, childLocation(index), metadata, locales),
          ]),
        ),
        required: children.filter((child) => child.required).map((child) => child.key),
        additionalProperties: false,
      };
    }
  }
}

function focusAfterChange(id: string) {
  requestAnimationFrame(() => document.getElementById(id)?.focus());
}

type NodeEditorProps = Readonly<{
  node: DraftNode;
  onChange: (node: DraftNode) => void;
  locales: readonly string[];
  copy: FieldCopy;
  id: string;
  depth: number;
  onTextValidity: (id: string, valid: boolean) => void;
}>;
function NodeEditor({ node, onChange, locales, copy, id, depth, onTextValidity }: NodeEditorProps) {
  const typeLabels = {
    string: copy.string,
    enum: copy.enum,
    integer: copy.integer,
    number: copy.number,
    boolean: copy.boolean,
    object: copy.object,
    array: copy.array,
  };
  return (
    <fieldset className="custom-field-group">
      <legend>{copy.type}</legend>
      <label htmlFor={id + '-type'}>{copy.type}</label>
      <select
        id={id + '-type'}
        value={node.type}
        onChange={(event) => {
          const type = event.target.value as DraftNode['type'];
          onChange({
            ...node,
            type,
            minimum:
              type === 'array' ? '0' : type === 'integer' || type === 'number' ? '-100' : '1',
            maximum:
              type === 'array' ? '3' : type === 'integer' || type === 'number' ? '100' : '32',
            item: type === 'array' ? (node.item ?? fresh()) : node.item,
          });
        }}
      >
        {(Object.keys(typeLabels) as (keyof typeof typeLabels)[])
          .filter((type) => depth < 2 || (type !== 'array' && type !== 'object'))
          .map((type) => (
            <option key={type} value={type}>
              {typeLabels[type]}
            </option>
          ))}
      </select>
      {locales.map((locale) => (
        <div className="auth-field" key={locale}>
          <label htmlFor={id + '-label-' + locale}>
            {copy.label} ({locale})
          </label>
          <CustomFieldTextEditor
            id={id + '-label-' + locale}
            value={node.labels[locale] ?? ''}
            lang={locale}
            copy={copy}
            maxBytes={128}
            onValidity={onTextValidity}
            onChange={(text) => onChange({ ...node, labels: { ...node.labels, [locale]: text } })}
          />
        </div>
      ))}
      {node.type !== 'boolean' && node.type !== 'object' && (
        <>
          <label htmlFor={id + '-min'}>{copy.min}</label>
          <input
            id={id + '-min'}
            type="text"
            inputMode="decimal"
            value={node.minimum}
            maxLength={128}
            onChange={(event) => onChange({ ...node, minimum: event.target.value })}
          />
          <label htmlFor={id + '-max'}>{copy.max}</label>
          <input
            id={id + '-max'}
            type="text"
            inputMode="decimal"
            value={node.maximum}
            maxLength={128}
            onChange={(event) => onChange({ ...node, maximum: event.target.value })}
          />
          {(node.type === 'number' || node.type === 'integer') && <p>{copy.numeric_hint}</p>}
        </>
      )}
      {node.type === 'enum' && (
        <>
          {node.choices.map((choice, index) => (
            <fieldset key={index}>
              <legend>
                {copy.item} {index + 1}
              </legend>
              <label htmlFor={id + '-choice-' + index}>{copy.choices}</label>
              <CustomFieldTextEditor
                id={id + '-choice-' + index}
                maxLength={256}
                copy={copy}
                onValidity={onTextValidity}
                value={choice.value}
                onChange={(text) =>
                  onChange({
                    ...node,
                    choices: node.choices.map((item, i) =>
                      i === index ? { ...item, value: text } : item,
                    ),
                  })
                }
              />
              {locales.map((locale) => (
                <div key={locale}>
                  <label htmlFor={id + '-choice-' + index + '-' + locale}>
                    {copy.choice_label} ({locale})
                  </label>
                  <CustomFieldTextEditor
                    id={id + '-choice-' + index + '-' + locale}
                    maxBytes={128}
                    lang={locale}
                    copy={copy}
                    onValidity={onTextValidity}
                    value={choice.labels[locale] ?? ''}
                    onChange={(text) =>
                      onChange({
                        ...node,
                        choices: node.choices.map((item, i) =>
                          i === index
                            ? { ...item, labels: { ...item.labels, [locale]: text } }
                            : item,
                        ),
                      })
                    }
                  />
                </div>
              ))}
              <button
                type="button"
                onClick={() => {
                  onChange({ ...node, choices: node.choices.filter((_, i) => i !== index) });
                  focusAfterChange(id + '-add-choice');
                }}
              >
                {copy.remove_property} {index + 1}
              </button>
            </fieldset>
          ))}
          {
            <button
              id={id + '-add-choice'}
              type="button"
              aria-disabled={node.choices.length >= 20}
              onClick={() => {
                if (node.choices.length >= 20) return;
                onChange({ ...node, choices: [...node.choices, { value: '', labels: {} }] });
                focusAfterChange(id + '-choice-' + node.choices.length);
              }}
            >
              {copy.add_property}
            </button>
          }
        </>
      )}
      {node.type === 'object' && (
        <>
          {node.children.map((child, index) => (
            <fieldset key={index}>
              <legend>
                {copy.item} {index + 1}
              </legend>
              <label htmlFor={id + '-key-' + index}>{copy.property}</label>
              <input
                id={id + '-key-' + index}
                value={child.key}
                maxLength={100}
                onChange={(event) =>
                  onChange({
                    ...node,
                    children: node.children.map((item, i) =>
                      i === index ? { ...item, key: event.target.value } : item,
                    ),
                  })
                }
              />
              <label>
                <input
                  type="checkbox"
                  checked={child.required}
                  onChange={(event) =>
                    onChange({
                      ...node,
                      children: node.children.map((item, i) =>
                        i === index ? { ...item, required: event.target.checked } : item,
                      ),
                    })
                  }
                />{' '}
                {copy.required}
              </label>
              <NodeEditor
                onTextValidity={onTextValidity}
                node={child.node}
                locales={locales}
                copy={copy}
                id={id + '-' + index}
                depth={depth + 1}
                onChange={(changed) =>
                  onChange({
                    ...node,
                    children: node.children.map((item, i) =>
                      i === index ? { ...item, node: changed } : item,
                    ),
                  })
                }
              />
              <button
                type="button"
                onClick={() => {
                  onChange({ ...node, children: node.children.filter((_, i) => i !== index) });
                  focusAfterChange(id + '-add-property');
                }}
              >
                {copy.remove_property} {index + 1}
              </button>
            </fieldset>
          ))}
          {
            <button
              id={id + '-add-property'}
              type="button"
              aria-disabled={node.children.length >= 10}
              onClick={() => {
                if (node.children.length >= 10) return;
                onChange({
                  ...node,
                  children: [...node.children, { key: '', required: false, node: fresh() }],
                });
                focusAfterChange(id + '-key-' + node.children.length);
              }}
            >
              {copy.add_property}
            </button>
          }
        </>
      )}
      {node.type === 'array' && (
        <NodeEditor
          onTextValidity={onTextValidity}
          node={node.item ?? fresh()}
          onChange={(item) => onChange({ ...node, item })}
          locales={locales}
          copy={copy}
          id={id + '-item'}
          depth={depth + 1}
        />
      )}
    </fieldset>
  );
}

type BuilderProps = Readonly<{
  initial?: CustomFieldDefinition;
  successor?: boolean;
  csrfToken: string;
  formKey: string;
  locales: readonly string[];
  defaultLocale: string;
  locale: string;
  copy: FieldCopy;
}>;
/** Progressive authoring; the submitted payload always contains a real schema object. */
export function CustomFieldBuilder({
  initial,
  successor = false,
  csrfToken,
  formKey,
  locales,
  defaultLocale,
  locale,
  copy,
}: BuilderProps) {
  const [enhanced, setEnhanced] = useState(false);
  useEffect(() => {
    setEnhanced(true);
  }, []);
  const admitted = initial ? readFieldSchema(initial.schema) : null;
  const [node, setNode] = useState(() =>
    admitted && initial
      ? draftFrom(admitted.properties.value, initial.labels, initial.formLabels)
      : fresh(),
  );
  const [schemaVersion, setSchemaVersion] = useState(
    String(initial ? initial.schemaVersion + (successor ? 1 : 0) : 1),
  );
  const [key, setKey] = useState(initial?.key ?? '');
  const [category, setCategory] = useState(initial?.piiCategory ?? '');
  const [required, setRequired] = useState(initial?.isRequired ?? false);
  const [sort, setSort] = useState(String(initial?.sort ?? 0));
  const [purpose, setPurpose] = useState(initial?.purposeKey ?? '');
  const [notices, setNotices] = useState<Record<string, string>>({ ...initial?.notices });
  const [error, setError] = useState(false);
  const [invalidText, setInvalidText] = useState<Readonly<Record<string, boolean>>>({});
  const onTextValidity = useCallback((id: string, valid: boolean) => {
    setInvalidText((previous) =>
      previous[id] === !valid ? previous : { ...previous, [id]: !valid },
    );
  }, []);
  const revise = initial !== undefined && !successor;
  const excludesDisabledTranslations = initial
    ? hasDisabledFieldTranslations(initial, locales)
    : false;
  let payload: CustomFieldInput | null = null;
  let schema: ReturnType<typeof envelopeSchema> | null = null;
  let metadata: ControlLabels[] = [];
  let bytes = 0;
  let schemaBytes = 0;
  try {
    const value = compile(node, '/', metadata, locales);
    schema = readFieldSchema(envelopeSchema(value));
    schemaBytes = new TextEncoder().encode(JSON.stringify(schema)).length;
    const labels = localized(node.labels, locales);
    const localizedNotices = localized(notices, locales);
    if (
      schema &&
      !Object.values(invalidText).some(Boolean) &&
      validFieldKey(key) &&
      labels[defaultLocale] &&
      category &&
      (category !== 'Sensitive' ||
        (purpose.length <= 64 && validFieldKey(purpose) && localizedNotices[defaultLocale])) &&
      metadata.every(
        (entry) =>
          (entry.labels === null || entry.labels[defaultLocale]) &&
          (!entry.choices || entry.choices.every((choice) => choice[defaultLocale])),
      )
    ) {
      const input = {
        targetEntity: 'Membership',
        key,
        schemaVersion: numeric(schemaVersion),
        schema,
        labels,
        piiCategory: category,
        isRequired: required,
        sort: numeric(sort),
        purposeKey: category === 'Sensitive' ? purpose : null,
        notices: category === 'Sensitive' ? localizedNotices : null,
        formLabels: metadata.length ? metadata : null,
      };
      bytes = new TextEncoder().encode(JSON.stringify(input)).length;
      if (
        bytes <= 4096 &&
        Number.isSafeInteger(input.schemaVersion) &&
        input.schemaVersion >= 1 &&
        input.schemaVersion <= 2147483647 &&
        Number.isSafeInteger(input.sort) &&
        input.sort >= 0 &&
        input.sort <= 32767 &&
        new TextEncoder().encode(JSON.stringify(schema)).length <= 2048 &&
        Object.values(labels).every((text) => new TextEncoder().encode(text).length <= 128) &&
        Object.values(localizedNotices).every(
          (text) => new TextEncoder().encode(text).length <= 512,
        ) &&
        metadata.every((entry) =>
          [
            ...Object.values(entry.labels ?? {}),
            ...(entry.choices ?? []).flatMap((choice) => Object.values(choice)),
          ].every((text) => new TextEncoder().encode(text).length <= 128),
        )
      )
        payload = input as CustomFieldInput;
    }
  } catch {
    schema = null;
    metadata = [];
  }
  function submit(event: FormEvent<HTMLFormElement>) {
    if (!payload || !event.currentTarget.checkValidity()) {
      event.preventDefault();
      setError(true);
    }
  }
  return (
    <>
      <form
        hidden={!enhanced}
        className="custom-field-editor custom-field-js-builder"
        action={revise ? '/api/studio/custom-field-revise' : '/api/studio/custom-field-create'}
        method="post"
        onSubmit={submit}
      >
        {excludesDisabledTranslations && (
          <p className="auth-notice">{copy.disabled_translations_hint}</p>
        )}
        <p>
          {copy.default_locale}: {defaultLocale}
        </p>
        <input type="hidden" name="csrfToken" value={csrfToken} />
        <input type="hidden" name="idempotencyKey" value={formKey} />
        {revise && (
          <>
            <input type="hidden" name="definitionId" value={initial.definitionId} />
            <input type="hidden" name="version" value={initial.version} />
          </>
        )}
        <input type="hidden" name="payload" value={payload ? JSON.stringify(payload) : ''} />
        <label htmlFor="definition-schema-version">{copy.revision}</label>
        <input
          id="definition-schema-version"
          type="text"
          inputMode="numeric"
          value={schemaVersion}
          maxLength={10}
          readOnly={revise}
          onChange={(event) => setSchemaVersion(event.target.value)}
        />
        <label htmlFor="definition-key">{copy.key}</label>
        <input
          id="definition-key"
          value={key}
          maxLength={100}
          readOnly={Boolean(initial)}
          onChange={(event) => setKey(event.target.value)}
        />
        <label htmlFor="definition-category">{copy.category}</label>
        <select
          id="definition-category"
          value={category}
          onChange={(event) => setCategory(event.target.value as CustomFieldInput['piiCategory'])}
          aria-required="true"
        >
          <option value="">{copy.choose}</option>
          <option value="None">{copy.none}</option>
          <option value="Identity">{copy.identity}</option>
          <option value="Behaviour">{copy.behaviour}</option>
          <option value="Sensitive">{copy.sensitive}</option>
        </select>
        <label>
          <input
            type="checkbox"
            checked={required}
            onChange={(event) => setRequired(event.target.checked)}
          />{' '}
          {copy.required}
        </label>
        <label htmlFor="definition-sort">{copy.order}</label>
        <input
          id="definition-sort"
          aria-describedby="definition-sort-hint"
          value={sort}
          onChange={(event) => setSort(event.target.value)}
          maxLength={6}
        />
        <p id="definition-sort-hint">
          {copy.min}: 0 · {copy.max}: 32767
        </p>
        {category === 'Sensitive' && (
          <>
            <label htmlFor="definition-purpose">{copy.purpose}</label>
            <input
              id="definition-purpose"
              value={purpose}
              maxLength={64}
              onChange={(event) => setPurpose(event.target.value)}
            />
            {locales.map((tag) => (
              <div key={tag}>
                <label htmlFor={'definition-notice-' + tag}>
                  {copy.notice} ({tag})
                </label>
                <CustomFieldTextEditor
                  id={'definition-notice-' + tag}
                  value={notices[tag] ?? ''}
                  lang={tag}
                  copy={copy}
                  maxBytes={512}
                  onValidity={onTextValidity}
                  onChange={(text) => setNotices({ ...notices, [tag]: text })}
                />
              </div>
            ))}
          </>
        )}
        <NodeEditor
          onTextValidity={onTextValidity}
          node={node}
          onChange={setNode}
          locales={locales}
          copy={copy}
          id="definition-control"
          depth={0}
        />
        <p>{copy.input_budget}</p>
        <output aria-live="polite">
          {bytes} / 4096 {copy.bytes} · {schemaBytes} / 2048 {copy.bytes}
        </output>
        {error && !payload && (
          <p role="alert">
            {copy.errors} {copy.unsupported}
          </p>
        )}
        <button type="submit">{revise ? copy.save_draft : copy.create}</button>
      </form>
      <section
        hidden={!enhanced}
        className="custom-field-js-builder"
        aria-labelledby="definition-preview-title"
      >
        <h3 id="definition-preview-title">{copy.preview}</h3>
        {schema ? (
          <FieldControls
            schema={schema.properties.value}
            labels={node.labels}
            formLabels={metadata}
            locale={locale}
            defaultLocale={defaultLocale}
            copy={copy}
            prefix="preview"
          />
        ) : (
          <p>{copy.unsupported}</p>
        )}
      </section>
    </>
  );
}
