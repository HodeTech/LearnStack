import type {
  MembershipCustomFields,
  LocalizedText,
  FormLabel,
} from '@learnstack/sdk/administration-server';
import {
  parseCustomFieldJson,
  parseCustomFieldNumber,
  decodeCustomFieldChoice,
  decodeCustomFieldText,
} from '@learnstack/sdk/administration-server';

import { BffInputError } from './input-error.js';
import type { NativeOperation } from './native-operation-contract.js';
const record = (value: unknown): value is Record<string, unknown> =>
  value !== null && typeof value === 'object' && !Array.isArray(value);
const fail = (): never => {
  throw new BffInputError();
};
const required = (fields: URLSearchParams, key: string) => fields.get(key) ?? fail();
const id = (value: string) =>
  /^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(value)
    ? value
    : fail();
const version = (value: string) =>
  /^(0|[1-9][0-9]{0,18})$/.test(value) && (value.length < 19 || value <= '9223372036854775807')
    ? value
    : fail();
function integer(value: string, zero = false): number {
  const result = Number(value);
  if (!/^(0|[1-9][0-9]*)$/.test(value) || !Number.isSafeInteger(result) || result < (zero ? 0 : 1))
    fail();
  return result;
}
const parse = (source: string, maximum = 4096) => {
  try {
    return parseCustomFieldJson(source, maximum);
  } catch {
    return fail();
  }
};
function declareCustomFieldControls(
  schema: unknown,
  prefix: string,
  depth: number,
  controls: Set<string>,
): void {
  if (!record(schema) || depth > 2) return fail();
  if (schema.type === 'object') {
    if (
      !record(schema.properties) ||
      !Array.isArray(schema.required) ||
      schema.additionalProperties !== false
    )
      return fail();
    const keys = Object.keys(schema.properties).sort();
    if (
      keys.length < 1 ||
      keys.length > 10 ||
      schema.required.some((key) => typeof key !== 'string' || !keys.includes(key))
    )
      return fail();
    keys.forEach((key, index) => {
      if (
        key.length > 100 ||
        /^[a-z0-9]+(?:-[a-z0-9]+)*$/.exec(key)?.[0] !== key ||
        ['constructor', 'prototype', '__proto__'].includes(key)
      )
        return fail();
      const path = prefix + '_' + index;
      if (!(schema.required as unknown[]).includes(key)) controls.add('presence_' + path);
      declareCustomFieldControls(
        (schema.properties as Record<string, unknown>)[key],
        path,
        depth + 1,
        controls,
      );
    });
    return;
  }
  if (schema.type === 'array') {
    if (
      !Number.isSafeInteger(schema.minItems) ||
      !Number.isSafeInteger(schema.maxItems) ||
      (schema.minItems as number) < 0 ||
      (schema.maxItems as number) < Math.max(1, schema.minItems as number) ||
      (schema.maxItems as number) > 10
    )
      return fail();
    controls.add('count_' + prefix);
    for (let index = 0; index < (schema.maxItems as number); ++index)
      declareCustomFieldControls(schema.items, prefix + '_' + index, depth + 1, controls);
    return;
  }
  if (!['string', 'boolean', 'integer', 'number'].includes(schema.type as string)) return fail();
  controls.add('answer_' + prefix);
  if (schema.type === 'string' && schema.enum === undefined) controls.add('textmode_' + prefix);
}

/** Fresh immutable metadata declares the finite controls, including explicitly excluded branches. */
export function buildCustomFieldAnswer(
  schema: unknown,
  fields: URLSearchParams,
  prefix = '0_root',
  depth = 0,
  consumed: Set<string> = new Set(),
): unknown {
  if (depth === 0) declareCustomFieldControls(schema, prefix, 0, consumed);
  if (!record(schema) || depth > 2 || typeof schema.type !== 'string') return fail();
  if (schema.type === 'object') {
    if (
      !record(schema.properties) ||
      !Array.isArray(schema.required) ||
      schema.additionalProperties !== false
    )
      return fail();
    const keys = Object.keys(schema.properties).sort();
    if (
      keys.length < 1 ||
      keys.length > 10 ||
      schema.required.some((key) => typeof key !== 'string' || !keys.includes(key))
    )
      return fail();
    const result: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
    keys.forEach((key, index) => {
      if (
        !(key.length <= 100 && /^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(key)) ||
        ['constructor', 'prototype', '__proto__'].includes(key)
      )
        fail();
      const path = prefix + '_' + index;
      if (schema.required instanceof Array && !schema.required.includes(key)) {
        consumed.add('presence_' + path);
        const present = required(fields, 'presence_' + path);
        if (present === 'off') return;
        if (present !== 'on') fail();
      }
      result[key] = buildCustomFieldAnswer(
        (schema.properties as Record<string, unknown>)[key],
        fields,
        path,
        depth + 1,
        consumed,
      );
    });
    return result;
  }
  if (schema.type === 'array') {
    if (
      !Number.isInteger(schema.minItems) ||
      !Number.isInteger(schema.maxItems) ||
      (schema.minItems as number) < 0 ||
      (schema.maxItems as number) > 10 ||
      (schema.maxItems as number) < 1
    )
      return fail();
    consumed.add('count_' + prefix);
    const count = integer(required(fields, 'count_' + prefix), true);
    if (count < (schema.minItems as number) || count > (schema.maxItems as number)) return fail();
    return Array.from({ length: count }, (_, index) =>
      buildCustomFieldAnswer(schema.items, fields, prefix + '_' + index, depth + 1, consumed),
    );
  }
  consumed.add('answer_' + prefix);
  const raw = required(fields, 'answer_' + prefix);
  if (schema.type === 'boolean') {
    if (raw !== 'true' && raw !== 'false') return fail();
    return raw === 'true';
  }
  if (schema.type === 'string') {
    let value: string;
    try {
      value =
        schema.enum === undefined
          ? decodeCustomFieldText(raw, required(fields, 'textmode_' + prefix))
          : Array.isArray(schema.enum) && schema.enum.every((choice) => typeof choice === 'string')
            ? decodeCustomFieldChoice(raw, schema.enum)
            : fail();
    } catch {
      return fail();
    }
    if (
      !Number.isInteger(schema.minLength) ||
      !Number.isInteger(schema.maxLength) ||
      (schema.maxLength as number) > 256 ||
      (schema.minLength as number) < 1 ||
      [...value].length < (schema.minLength as number) ||
      [...value].length > (schema.maxLength as number)
    )
      return fail();
    return value;
  }
  if (schema.type === 'integer' || schema.type === 'number') {
    let number: number;
    try {
      number = parseCustomFieldNumber(raw, schema.type === 'integer');
    } catch {
      return fail();
    }
    if (
      typeof number !== 'number' ||
      (schema.type === 'integer' && !Number.isSafeInteger(number)) ||
      typeof schema.minimum !== 'number' ||
      typeof schema.maximum !== 'number' ||
      number < schema.minimum ||
      number > schema.maximum
    )
      return fail();
    return number;
  }
  return fail();
}
export const CUSTOM_FIELD_FORMS: Readonly<Record<string, readonly string[]>> = Object.freeze({
  '/api/studio/custom-field-create': [
    'csrfToken',
    'idempotencyKey',
    'payload',
    'authorKey',
    'schemaVersion',
    'authorType',
    'authorMinimum',
    'authorMaximum',
    'authorMinLength',
    'authorMaxLength',
    'authorCategory',
    'authorRequired',
    'authorSort',
    'authorPurpose',
    'authorFormLabels',
  ],
  '/api/studio/custom-field-revise': [
    'csrfToken',
    'idempotencyKey',
    'version',
    'definitionId',
    'payload',
    'authorKey',
    'schemaVersion',
    'authorType',
    'authorMinimum',
    'authorMaximum',
    'authorMinLength',
    'authorMaxLength',
    'authorCategory',
    'authorRequired',
    'authorSort',
    'authorPurpose',
    'authorFormLabels',
  ],
  '/api/studio/custom-field-publish': [
    'csrfToken',
    'idempotencyKey',
    'version',
    'definitionId',
    'expectedIncumbentId',
    'expectedIncumbentVersion',
  ],
  '/api/studio/custom-field-retire': ['csrfToken', 'idempotencyKey', 'version', 'definitionId'],
  '/api/studio/custom-field-discard': ['csrfToken', 'idempotencyKey', 'version', 'definitionId'],
  '/api/studio/membership-custom-fields': [
    'csrfToken',
    'idempotencyKey',
    'version',
    'membershipId',
    'payload',
  ],
  '/api/account/custom-fields': ['csrfToken', 'idempotencyKey', 'version', 'payload'],
  '/api/account/consent-grant': [
    'csrfToken',
    'idempotencyKey',
    'definitionId',
    'definitionVersion',
    'noticeLocale',
    'expectedNoticeHash',
    'expectedSequence',
    'expectedEvidenceId',
    'confirmed',
  ],
  '/api/account/consent-withdraw': [
    'csrfToken',
    'idempotencyKey',
    'membershipId',
    'evidenceId',
    'expectedSequence',
    'expectedEvidenceId',
    'confirmed',
  ],
});
export const customAuthorName = (name: string): boolean =>
  /^author(?:Label|Notice|TextMode_(?:Label|Notice))_[a-zA-Z]{2,8}(?:-[a-zA-Z0-9]{1,8})*$/.exec(
    name,
  )?.[0] === name;
export const customControlName = (name: string): boolean =>
  /^(?:answer|presence|count|textmode)_[0-9]{1,2}_root(?:_[0-9]){0,2}$/.test(name);
export function customFieldForm(
  path: string,
  fields: URLSearchParams,
  metadata?: MembershipCustomFields,
): Readonly<{ operation: NativeOperation; destination: string }> {
  const allowed = CUSTOM_FIELD_FORMS[path];
  const names = [...fields.keys()];
  if (
    !allowed ||
    names.length > 2500 ||
    new Set(names).size !== names.length ||
    names.some(
      (name) =>
        !allowed.includes(name) &&
        !(
          (path === '/api/studio/custom-field-create' ||
            path === '/api/studio/custom-field-revise') &&
          customAuthorName(name)
        ) &&
        !(
          (path === '/api/account/custom-fields' ||
            path === '/api/studio/membership-custom-fields') &&
          customControlName(name)
        ),
    )
  )
    return fail();
  const idempotencyKey = required(fields, 'idempotencyKey');
  if (!/^[\x21-\x7e]{8,128}$/.test(idempotencyKey)) return fail();
  if (path === '/api/account/consent-grant' || path === '/api/account/consent-withdraw') {
    const expectedSequence = integer(required(fields, 'expectedSequence'), true);
    const expectedEvidenceId =
      required(fields, 'expectedEvidenceId') === ''
        ? null
        : id(required(fields, 'expectedEvidenceId'));
    if (required(fields, 'confirmed') !== 'true') return fail();
    if (path.endsWith('grant'))
      return {
        operation: {
          kind: 'grantOwnFieldConsent',
          idempotencyKey,
          input: {
            definitionId: id(required(fields, 'definitionId')),
            definitionVersion: integer(required(fields, 'definitionVersion')),
            noticeLocale: required(fields, 'noticeLocale'),
            expectedNoticeHash: required(fields, 'expectedNoticeHash'),
            expectedSequence,
            expectedEvidenceId,
            confirmed: true,
          },
        },
        destination: '/portal/profile',
      };
    if (expectedSequence < 1 || expectedEvidenceId === null) return fail();
    return {
      operation: {
        kind: 'withdrawOwnFieldConsent',
        idempotencyKey,
        membershipId: id(required(fields, 'membershipId')),
        input: {
          evidenceId: id(required(fields, 'evidenceId')),
          expectedSequence,
          expectedEvidenceId,
          confirmed: true,
        },
      },
      destination: '/portal/profile',
    };
  }
  if (path.endsWith('custom-fields')) {
    let payload = required(fields, 'payload');
    const parsed = parse(payload);
    if (!record(parsed) || !Array.isArray(parsed.set) || !Array.isArray(parsed.remove))
      return fail();
    if (names.some(customControlName)) {
      if (!metadata || parsed.set.length > 20) return fail();
      const allowedControls = new Set<string>();
      parsed.set.forEach((item: unknown, index: number) => {
        if (
          !record(item) ||
          !record(item.envelope) ||
          Object.keys(item.envelope).length !== 1 ||
          item.envelope.value !== null
        )
          return fail();
        const field = metadata.fields.find(
          (field) =>
            field.definitionId === item.definitionId &&
            field.schemaVersion === item.schemaVersion &&
            field.conceptKey === item.conceptKey &&
            ['Active', 'Deprecated'].includes(field.definition.status),
        );
        if (!field || !record(field.definition.schema.properties)) return fail();
        const shape = field.definition.schema.properties.value;
        item.envelope = {
          value: buildCustomFieldAnswer(shape, fields, index + '_root', 0, allowedControls),
        };
      });
      if (names.some((name) => customControlName(name) && !allowedControls.has(name)))
        return fail();
      payload = JSON.stringify(parsed);
      parse(payload);
    }
    const write = { version: version(required(fields, 'version')), idempotencyKey };
    return path === '/api/account/custom-fields'
      ? {
          operation: { kind: 'setOwnCustomFields', patch: payload, ...write },
          destination: '/portal/profile',
        }
      : {
          operation: {
            kind: 'setMembershipCustomFields',
            id: id(required(fields, 'membershipId')),
            patch: payload,
            ...write,
          },
          destination: '/studio/members/' + id(required(fields, 'membershipId')),
        };
  }
  const authorPayload = () => {
    if (fields.has('payload')) {
      if (names.some((name) => name.startsWith('author'))) return fail();
      const raw = required(fields, 'payload');
      parse(raw);
      return raw;
    }
    const type = required(fields, 'authorType');
    if (!['string', 'integer', 'number', 'boolean'].includes(type)) return fail();
    const value: Record<string, unknown> = { type };
    if (type === 'string') {
      value.minLength = integer(required(fields, 'authorMinLength'));
      value.maxLength = integer(required(fields, 'authorMaxLength'));
    }
    if (type === 'integer' || type === 'number') {
      try {
        value.minimum = parseCustomFieldNumber(
          required(fields, 'authorMinimum'),
          type === 'integer',
        );
        value.maximum = parseCustomFieldNumber(
          required(fields, 'authorMaximum'),
          type === 'integer',
        );
      } catch {
        return fail();
      }
    }
    const labels: Record<string, string> = Object.create(null) as Record<string, string>;
    const notices: Record<string, string> = Object.create(null) as Record<string, string>;
    for (const [name, content] of fields) {
      const mode = /^authorTextMode_(Label|Notice)_(.+)$/.exec(name);
      if (mode && !fields.has('author' + mode[1] + '_' + mode[2])) return fail();
      const field = /^author(Label|Notice)_(.+)$/.exec(name);
      if (!field) continue;
      let decoded: string;
      try {
        decoded = decodeCustomFieldText(
          content,
          required(fields, 'authorTextMode_' + field[1] + '_' + field[2]),
        );
      } catch {
        return fail();
      }
      if (new TextEncoder().encode(decoded).length > (field[1] === 'Label' ? 128 : 512))
        return fail();
      if (decoded !== '') (field[1] === 'Label' ? labels : notices)[field[2]!] = decoded;
    }
    if (fields.has('authorRequired') && fields.get('authorRequired') !== 'on') return fail();
    const purpose = required(fields, 'authorPurpose');
    const sort = parse(required(fields, 'authorSort'));
    if (typeof sort !== 'number' || !Number.isSafeInteger(sort)) return fail();
    const raw = JSON.stringify({
      targetEntity: 'Membership',
      key: required(fields, 'authorKey'),
      schemaVersion: integer(required(fields, 'schemaVersion')),
      schema: {
        $schema: 'https://json-schema.org/draft/2020-12/schema',
        type: 'object',
        properties: { value },
        required: ['value'],
        additionalProperties: false,
      },
      labels,
      piiCategory: required(fields, 'authorCategory'),
      isRequired: fields.get('authorRequired') === 'on',
      sort,
      purposeKey: purpose === '' ? null : purpose,
      notices: Object.keys(notices).length ? notices : null,
      formLabels: parse(fields.get('authorFormLabels') ?? 'null'),
    });
    parse(raw);
    return raw;
  };
  const destination = '/studio/custom-fields';
  if (path.endsWith('create')) {
    const payload = authorPayload();
    return {
      operation: { kind: 'registerCustomFieldDefinition', input: payload, idempotencyKey },
      destination,
    };
  }
  const definitionId = id(required(fields, 'definitionId'));
  const write = { version: version(required(fields, 'version')), idempotencyKey };
  if (path.endsWith('revise')) {
    const payload = authorPayload();
    return {
      operation: {
        kind: 'reviseCustomFieldDefinition',
        id: definitionId,
        input: payload,
        ...write,
      },
      destination: destination + '/' + definitionId,
    };
  }
  if (path.endsWith('publish')) {
    const incumbent = required(fields, 'expectedIncumbentId');
    const incumbentVersion = required(fields, 'expectedIncumbentVersion');
    if ((incumbent === '') !== (incumbentVersion === '')) return fail();
    return {
      operation: {
        kind: 'publishCustomFieldDefinition',
        id: definitionId,
        expectedIncumbentId: incumbent === '' ? null : id(incumbent),
        expectedIncumbentVersion: incumbentVersion === '' ? null : version(incumbentVersion),
        ...write,
      },
      destination: destination + '/' + definitionId,
    };
  }
  return {
    operation: {
      kind: path.endsWith('retire')
        ? 'retireCustomFieldDefinition'
        : 'discardCustomFieldDefinition',
      id: definitionId,
      ...write,
    },
    destination: destination + '/' + definitionId,
  };
}
/** Response-only recovery metadata; schemas never travel in the submitted form. */
export function customFieldControlDescriptions(
  fields: URLSearchParams,
  metadata: MembershipCustomFields,
): readonly Readonly<{
  index: number;
  schema: unknown;
  labels: LocalizedText;
  formLabels: readonly FormLabel[] | null;
}>[] {
  try {
    const payload = parseCustomFieldJson(fields.get('payload') ?? '');
    if (!record(payload) || !Array.isArray(payload.set)) return [];
    const descriptions = payload.set.map((pin: unknown, index: number) => {
      if (!record(pin)) return null;
      const field = metadata.fields.find(
        (field) =>
          field.definitionId === pin.definitionId &&
          field.schemaVersion === pin.schemaVersion &&
          field.conceptKey === pin.conceptKey &&
          field.definition.status === 'Active',
      );
      return field
        ? {
            index,
            schema: field.definition.schema,
            labels: field.definition.labels,
            formLabels: field.definition.formLabels,
          }
        : null;
    });
    return descriptions.some((entry) => entry === null)
      ? []
      : descriptions.filter((entry) => entry !== null);
  } catch {
    return [];
  }
}
