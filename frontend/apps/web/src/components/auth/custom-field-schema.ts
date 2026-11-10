/** The finite ADR-0095 profile shared by authoring preview and member controls. */
export type FieldSchema =
  | Readonly<{ type: 'string'; minLength: number; maxLength: number; enum?: readonly string[] }>
  | Readonly<{ type: 'integer' | 'number'; minimum: number; maximum: number }>
  | Readonly<{ type: 'boolean' }>
  | Readonly<{
      type: 'object';
      properties: Readonly<Record<string, FieldSchema>>;
      required: readonly string[];
      additionalProperties: false;
    }>
  | Readonly<{ type: 'array'; items: FieldSchema; minItems: number; maxItems: number }>;
export type FieldEnvelopeSchema = Readonly<{
  $schema: 'https://json-schema.org/draft/2020-12/schema';
  type: 'object';
  properties: Readonly<{ value: FieldSchema }>;
  required: readonly ['value'];
  additionalProperties: false;
}>;
export type ControlLabels = Readonly<{
  location: string;
  labels: Readonly<Record<string, string>> | null;
  choices?: readonly Readonly<Record<string, string>>[] | null;
}>;
const record = (value: unknown): value is Record<string, unknown> =>
  value !== null && typeof value === 'object' && !Array.isArray(value);
const whole = (value: unknown, min: number, max: number): value is number =>
  typeof value === 'number' && Number.isSafeInteger(value) && value >= min && value <= max;
const supportedNumber = (value: unknown): value is number =>
  typeof value === 'number' &&
  Number.isFinite(value) &&
  (!Number.isInteger(value) || Number.isSafeInteger(value));
export const validFieldKey = (key: string) =>
  key.length >= 1 &&
  key.length <= 100 &&
  /^[a-z0-9]+(?:-[a-z0-9]+)*$/.exec(key)?.[0] === key &&
  !['constructor', 'prototype', '__proto__'].includes(key);
const keys = (value: Record<string, unknown>, allowed: readonly string[]) =>
  Object.keys(value).every((key) => allowed.includes(key));
export const fieldPropertyKeys = (schema: Extract<FieldSchema, { type: 'object' }>) =>
  Object.keys(schema.properties).sort();

/** Refuse unsupported shapes explicitly; never interpret ignored JSON Schema keywords. */
export function readFieldSchema(value: unknown): FieldEnvelopeSchema | null {
  if (
    !record(value) ||
    !keys(value, ['$schema', 'type', 'properties', 'required', 'additionalProperties']) ||
    value.$schema !== 'https://json-schema.org/draft/2020-12/schema' ||
    value.type !== 'object' ||
    value.additionalProperties !== false ||
    !record(value.properties) ||
    Object.keys(value.properties).join() !== 'value' ||
    !Array.isArray(value.required) ||
    value.required.length !== 1 ||
    value.required[0] !== 'value'
  )
    return null;
  function valid(node: unknown, containers: number): node is FieldSchema {
    if (!record(node)) return false;
    switch (node.type) {
      case 'string':
        return (
          keys(node, ['type', 'minLength', 'maxLength', 'enum']) &&
          whole(node.minLength, 1, 256) &&
          whole(node.maxLength, node.minLength, 256) &&
          (node.enum === undefined ||
            (Array.isArray(node.enum) &&
              node.enum.length >= 1 &&
              node.enum.length <= 20 &&
              new Set(node.enum).size === node.enum.length &&
              node.enum.every(
                (choice) =>
                  typeof choice === 'string' &&
                  [...choice].length >= (node.minLength as number) &&
                  [...choice].length <= (node.maxLength as number),
              )))
        );
      case 'integer':
      case 'number':
        return (
          keys(node, ['type', 'minimum', 'maximum']) &&
          supportedNumber(node.minimum) &&
          supportedNumber(node.maximum) &&
          node.minimum <= node.maximum &&
          (node.type !== 'integer' ||
            (Number.isSafeInteger(node.minimum) && Number.isSafeInteger(node.maximum)))
        );
      case 'boolean':
        return keys(node, ['type']);
      case 'object': {
        if (
          containers >= 2 ||
          !keys(node, ['type', 'properties', 'required', 'additionalProperties']) ||
          node.additionalProperties !== false ||
          !record(node.properties) ||
          !Array.isArray(node.required)
        )
          return false;
        const names = Object.keys(node.properties);
        return (
          names.length >= 1 &&
          names.length <= 10 &&
          names.every((key) => validFieldKey(key)) &&
          new Set(node.required).size === node.required.length &&
          node.required.every((key) => typeof key === 'string' && names.includes(key)) &&
          Object.values(node.properties).every((child) => valid(child, containers + 1))
        );
      }
      case 'array':
        return (
          containers < 2 &&
          keys(node, ['type', 'items', 'minItems', 'maxItems']) &&
          whole(node.minItems, 0, 10) &&
          whole(node.maxItems, Math.max(1, node.minItems), 10) &&
          valid(node.items, containers + 1)
        );
      default:
        return false;
    }
  }
  return valid(value.properties.value, 0) ? (value as FieldEnvelopeSchema) : null;
}

export function envelopeSchema(value: FieldSchema): FieldEnvelopeSchema {
  return {
    $schema: 'https://json-schema.org/draft/2020-12/schema',
    type: 'object',
    properties: { value },
    required: ['value'],
    additionalProperties: false,
  };
}
export function authoredText(
  labels: Readonly<Record<string, string>>,
  locale: string,
  defaultLocale: string,
): Readonly<{ text: string; lang: string }> {
  const lang = Object.hasOwn(labels, locale)
    ? locale
    : Object.hasOwn(labels, defaultLocale)
      ? defaultLocale
      : (Object.keys(labels).sort()[0] ?? defaultLocale);
  return { text: labels[lang] ?? '', lang };
}

/** Explicit draft saves carry only the languages the editor currently presents. */
export function hasDisabledFieldTranslations(
  metadata: Readonly<{
    labels: Readonly<Record<string, string>>;
    notices?: Readonly<Record<string, string>> | null;
    formLabels?: readonly ControlLabels[] | null;
  }>,
  locales: readonly string[],
): boolean {
  const authored = [
    ...Object.keys(metadata.labels),
    ...Object.keys(metadata.notices ?? {}),
    ...(metadata.formLabels ?? []).flatMap((entry) => [
      ...Object.keys(entry.labels ?? {}),
      ...(entry.choices ?? []).flatMap((choice) => Object.keys(choice)),
    ]),
  ];
  return authored.some((tag) => !locales.includes(tag));
}

/** A member form never invents labels when immutable control metadata is unavailable. */
export function fieldControlLabelsSupported(
  schema: FieldSchema,
  labels?: readonly ControlLabels[] | null,
): boolean {
  const expected: { location: string; choices: number }[] = [];
  function visit(node: FieldSchema, location: string) {
    const choices = node.type === 'string' ? (node.enum?.length ?? 0) : 0;
    if (location !== '/' || choices > 0) expected.push({ location, choices });
    const child = (index: number) => location + (location === '/' ? '' : '/') + index;
    if (node.type === 'object')
      fieldPropertyKeys(node).forEach((key, index) => visit(node.properties[key]!, child(index)));
    if (node.type === 'array') visit(node.items, child(0));
  }
  visit(schema, '/');
  const actual = labels ?? [];
  const localized = (value: Readonly<Record<string, string>> | null | undefined) =>
    value !== null && value !== undefined && Object.values(value).some((text) => text.length > 0);
  return (
    actual.length === expected.length &&
    new Set(actual.map((entry) => entry.location)).size === actual.length &&
    expected.every((shape) => {
      const entry = actual.find((item) => item.location === shape.location);
      return (
        entry !== undefined &&
        (shape.location === '/' ? entry.labels === null : localized(entry.labels)) &&
        (shape.choices === 0
          ? entry.choices === undefined || entry.choices === null
          : entry.choices?.length === shape.choices && entry.choices.every(localized))
      );
    })
  );
}
