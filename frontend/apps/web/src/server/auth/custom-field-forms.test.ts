import { customFieldTextMode, encodeCustomFieldText } from '@learnstack/sdk/administration-server';
import type { JsonValue, MembershipCustomFields } from '@learnstack/sdk/administration-server';
import { describe, expect, it } from 'vitest';

import { buildCustomFieldAnswer, customFieldForm } from './custom-field-forms';
const id = '019a154b-0000-7000-8000-000000000011';
const schema = {
  type: 'object',
  properties: {
    'child-name': { type: 'number', minimum: -10, maximum: 10 },
    optional: { type: 'string', minLength: 1, maxLength: 32 },
  },
  required: ['child-name'],
  additionalProperties: false,
};
function nativeMetadata(shape: Readonly<Record<string, JsonValue>>): MembershipCustomFields {
  return {
    membershipId: id,
    version: '1',
    fields: [
      {
        conceptKey: 'answer',
        definitionId: id,
        schemaVersion: 1,
        state: 'Missing',
        isRequired: true,
        consent: null,
        envelope: null,
        definition: {
          schema: {
            $schema: 'https://json-schema.org/draft/2020-12/schema',
            type: 'object',
            properties: { value: shape },
            required: ['value'],
            additionalProperties: false,
          },
          labels: { en: 'Answer' },
          piiCategory: 'None',
          sort: 0,
          purposeKey: null,
          notices: null,
          formLabels: null,
          noticeHashes: null,
          status: 'Active',
        },
      },
    ],
  };
}
function nativeFields(): URLSearchParams {
  return new URLSearchParams({
    csrfToken: 'proof',
    version: '1',
    idempotencyKey: 'custom-key',
    payload: JSON.stringify({
      set: [
        {
          conceptKey: 'answer',
          definitionId: id,
          schemaVersion: 1,
          envelope: { value: null },
          consentEvidenceId: null,
        },
      ],
      remove: [],
    }),
  });
}

describe('native finite no-JS answer reconstruction', () => {
  it('uses ordinal paths and explicit optional absence', () => {
    const fields = new URLSearchParams({ answer_0_root_0: '1.25', presence_0_root_1: 'off' });
    expect(buildCustomFieldAnswer(schema, fields)).toEqual({ 'child-name': 1.25 });
  });
  it.each(['1e-400', '1e400', '9007199254740992'])('rejects lossy numeric %s', (raw) =>
    expect(() =>
      buildCustomFieldAnswer(
        { type: 'number', minimum: -1e20, maximum: 1e20 },
        new URLSearchParams({ answer_0_root: raw }),
      ),
    ).toThrow(),
  );
  it('reconstructs complete required sets and keeps explicit removal separate', () => {
    const pin = { conceptKey: 'answer', definitionId: id, schemaVersion: 1 };
    const fields = new URLSearchParams({
      csrfToken: 'proof',
      version: '1',
      idempotencyKey: 'custom-key',
      payload: JSON.stringify({
        set: [{ ...pin, envelope: { value: null }, consentEvidenceId: null }],
        remove: [],
      }),
      answer_0_root: 'false',
    });
    const metadata: MembershipCustomFields = {
      membershipId: id,
      version: '1',
      fields: [
        {
          ...pin,
          state: 'Missing',
          isRequired: true,
          consent: null,
          envelope: null,
          definition: {
            schema: {
              $schema: 'https://json-schema.org/draft/2020-12/schema',
              type: 'object',
              properties: { value: { type: 'boolean' } },
              required: ['value'],
              additionalProperties: false,
            },
            labels: { en: 'Answer' },
            piiCategory: 'None',
            sort: 0,
            purposeKey: null,
            notices: null,
            formLabels: null,
            noticeHashes: null,
            status: 'Active',
          },
        },
      ],
    };
    const result = customFieldForm('/api/account/custom-fields', fields, metadata);
    expect(result.operation.kind).toBe('setOwnCustomFields');
    if (result.operation.kind === 'setOwnCustomFields')
      expect(JSON.parse(result.operation.patch as string).set[0].envelope).toEqual({
        value: false,
      });
  });
  it('ignores only finite schema-declared controls in excluded optional branches and unused list items', () => {
    const metadata = nativeMetadata({
      type: 'object',
      properties: {
        optional: {
          type: 'array',
          minItems: 0,
          maxItems: 2,
          items: { type: 'number', minimum: 0, maximum: 10 },
        },
        required: {
          type: 'array',
          minItems: 1,
          maxItems: 2,
          items: { type: 'integer', minimum: 0, maximum: 10 },
        },
      },
      required: ['required'],
      additionalProperties: false,
    });
    const fields = nativeFields();
    for (const [name, value] of Object.entries({
      presence_0_root_0: 'off',
      count_0_root_0: '2',
      answer_0_root_0_0: 'UNUSED_CANARY',
      answer_0_root_0_1: '1e999',
      count_0_root_1: '1',
      answer_0_root_1_0: '1.00',
      answer_0_root_1_1: 'UNUSED_LAST',
    }))
      fields.set(name, value);
    const result = customFieldForm('/api/account/custom-fields', fields, metadata);
    if (result.operation.kind !== 'setOwnCustomFields') throw new Error('Wrong form operation');
    expect(JSON.parse(result.operation.patch as string).set[0].envelope).toEqual({
      value: { required: [1] },
    });
    expect(result.operation.patch).not.toContain('UNUSED');
    for (const unknown of ['answer_0_root_0_2', 'presence_0_root_1', 'answer_1_root_0']) {
      const hostile = new URLSearchParams(fields);
      hostile.set(unknown, 'ignored-looking');
      expect(() => customFieldForm('/api/account/custom-fields', hostile, metadata)).toThrow();
    }
    const duplicate = new URLSearchParams(fields);
    duplicate.append('answer_0_root_0_1', 'duplicate');
    expect(() => customFieldForm('/api/account/custom-fields', duplicate, metadata)).toThrow();
    const missing = new URLSearchParams(fields);
    missing.delete('answer_0_root_1_0');
    expect(() => customFieldForm('/api/account/custom-fields', missing, metadata)).toThrow();
    const lossy = new URLSearchParams(fields);
    lossy.set('answer_0_root_1_0', '1.0000000000000000001');
    expect(() => customFieldForm('/api/account/custom-fields', lossy, metadata)).toThrow();
  });

  it('normalizes an identical explicit retry through its exact retained Deprecated schema without granting new writes', () => {
    const active = nativeMetadata({ type: 'boolean' });
    const fields = nativeFields();
    fields.set('answer_0_root', 'true');
    const original = customFieldForm('/api/account/custom-fields', fields, active);
    const retired: MembershipCustomFields = {
      ...active,
      version: '99',
      fields: active.fields.map((field) => ({
        ...field,
        state: 'Retired',
        envelope: { value: true },
        definition: { ...field.definition, status: 'Deprecated' },
      })),
    };
    const replay = customFieldForm('/api/account/custom-fields', fields, retired);
    expect(replay.operation).toEqual(original.operation);
    expect(replay.operation).toMatchObject({ version: '1', idempotencyKey: 'custom-key' });
    const differentPin: MembershipCustomFields = {
      ...retired,
      fields: retired.fields.map((field) => ({ ...field, schemaVersion: 2 })),
    };
    expect(() => customFieldForm('/api/account/custom-fields', fields, differentPin)).toThrow();
    expect(() =>
      customFieldForm('/api/account/custom-fields', fields, { ...retired, fields: [] }),
    ).toThrow();
    for (const status of ['Draft', 'Discarded', 'Unknown']) {
      const unavailable = {
        ...retired,
        fields: retired.fields.map((field) => ({
          ...field,
          definition: { ...field.definition, status },
        })),
      } as unknown as MembershipCustomFields;
      expect(() => customFieldForm('/api/account/custom-fields', fields, unavailable)).toThrow();
    }
  });

  it.each([
    'plain',
    'line\nfeed',
    'carriage\rreturn',
    'paired\r\nlines',
    'mixed\r\nthen\nthen\r',
    '\nstart\n',
    'literal\\n\\r\\',
  ])('decodes typed string transport without changing admitted values', (value) => {
    const mode = customFieldTextMode(value);
    const encoded = encodeCustomFieldText(value, mode).replace(/\n/g, '\r\n');
    expect(
      buildCustomFieldAnswer(
        { type: 'string', minLength: 1, maxLength: 32 },
        new URLSearchParams({ answer_0_root: encoded, textmode_0_root: mode }),
      ) === value,
    ).toBe(true);
  });
  it('counts decoded text and rejects malformed escapes or an unspecified text mode', () => {
    expect(
      buildCustomFieldAnswer(
        { type: 'string', minLength: 1, maxLength: 1 },
        new URLSearchParams({ answer_0_root: '\\r', textmode_0_root: 'escaped' }),
      ),
    ).toBe('\r');
    for (const fields of [
      new URLSearchParams({ answer_0_root: '\\q', textmode_0_root: 'escaped' }),
      new URLSearchParams({ answer_0_root: '\\', textmode_0_root: 'escaped' }),
      new URLSearchParams({ answer_0_root: 'text' }),
      new URLSearchParams({ answer_0_root: 'text', textmode_0_root: 'unknown' }),
    ])
      expect(() =>
        buildCustomFieldAnswer({ type: 'string', minLength: 1, maxLength: 32 }, fields),
      ).toThrow();
  });
  it('resolves opaque enum indices to exact immutable CR/LF literals', () => {
    const choices = ['one\rtwo', 'one\r\ntwo', 'one\ntwo'];
    const shape = { type: 'string', minLength: 1, maxLength: 32, enum: choices };
    choices.forEach((choice, index) =>
      expect(
        buildCustomFieldAnswer(shape, new URLSearchParams({ answer_0_root: 'choice-' + index })) ===
          choice,
      ).toBe(true),
    );
    for (const token of ['choice-01', 'choice-3', 'choice-0\n', choices[0]!])
      expect(() =>
        buildCustomFieldAnswer(shape, new URLSearchParams({ answer_0_root: token })),
      ).toThrow();
  });

  it('allows removal-only without requesting other required values', () => {
    const fields = new URLSearchParams({
      csrfToken: 'proof',
      version: '1',
      idempotencyKey: 'custom-key',
      payload: JSON.stringify({
        set: [],
        remove: [{ conceptKey: 'answer', definitionId: id, schemaVersion: 1 }],
      }),
    });
    expect(customFieldForm('/api/account/custom-fields', fields).operation.kind).toBe(
      'setOwnCustomFields',
    );
  });
  it('refuses duplicate proof and arbitrary control names', () => {
    const fields = new URLSearchParams('csrfToken=a&csrfToken=b&idempotencyKey=custom-key');
    expect(() => customFieldForm('/api/account/custom-fields', fields)).toThrow();
  });
  it('creates structured scalar definitions without executing JavaScript', () => {
    const fields = new URLSearchParams({
      csrfToken: 'proof',
      idempotencyKey: 'custom-key',
      authorKey: 'score',
      schemaVersion: '1',
      authorType: 'number',
      authorMinimum: '-1.25',
      authorMaximum: '2.5',
      authorCategory: 'None',
      authorSort: '0',
      authorPurpose: '',
      authorLabel_en: 'Score',
      authorTextMode_Label_en: 'plain',
    });
    const result = customFieldForm('/api/studio/custom-field-create', fields);
    expect(result.operation.kind).toBe('registerCustomFieldDefinition');
    if (result.operation.kind === 'registerCustomFieldDefinition')
      expect(JSON.parse(result.operation.input as string).schema.properties.value).toEqual({
        type: 'number',
        minimum: -1.25,
        maximum: 2.5,
      });
  });
  const authorFields = () =>
    new URLSearchParams({
      csrfToken: 'proof',
      idempotencyKey: 'custom-key',
      authorKey: 'exact',
      schemaVersion: '1',
      authorType: 'boolean',
      authorCategory: 'Sensitive',
      authorSort: '0',
      authorPurpose: 'purpose',
      authorLabel_en: 'Label',
      authorTextMode_Label_en: 'plain',
      authorNotice_en: '',
      authorTextMode_Notice_en: 'plain',
    });
  it.each([
    'normal',
    'line\nfeed',
    'carriage\rreturn',
    'windows\r\nlines',
    'mixed\r\nthen\nthen\r',
    '\nstart\n',
    'back\\slash',
  ])('decodes native author labels and notices without changing admitted text bytes', (value) => {
    const fields = authorFields();
    const mode = customFieldTextMode(value);
    for (const kind of ['Label', 'Notice']) {
      fields.set(
        'author' + kind + '_en',
        encodeCustomFieldText(value, mode).replace(/\n/g, '\r\n'),
      );
      fields.set('authorTextMode_' + kind + '_en', mode);
    }
    const result = customFieldForm('/api/studio/custom-field-create', fields);
    if (result.operation.kind !== 'registerCustomFieldDefinition')
      throw new Error('Wrong operation');
    const proposal = JSON.parse(String(result.operation.input));
    expect(proposal.labels).toEqual({ en: value });
    expect(proposal.notices).toEqual({ en: value });
  });
  it('checks decoded author bytes, permits an empty optional notice, and rejects malformed or unpaired text controls', () => {
    const fields = authorFields();
    fields.set('authorLabel_en', '\\r'.repeat(128));
    fields.set('authorTextMode_Label_en', 'escaped');
    const accepted = customFieldForm('/api/studio/custom-field-create', fields);
    if (accepted.operation.kind !== 'registerCustomFieldDefinition')
      throw new Error('Wrong operation');
    expect(JSON.parse(String(accepted.operation.input))).toMatchObject({
      labels: { en: '\r'.repeat(128) },
      notices: null,
    });
    for (const [name, value] of [
      ['authorLabel_en', '\\r'.repeat(129)],
      ['authorLabel_en', '\\q'],
      ['authorNotice_en', '\\'],
      ['authorTextMode_Label_en', 'unknown'],
      ['authorTextMode_Notice_fr', 'plain'],
      ['authorNotice_en', 'x'.repeat(513)],
    ]) {
      const invalid = new URLSearchParams(fields);
      invalid.set(name!, value!);
      if (name === 'authorNotice_en' && value === '\\')
        invalid.set('authorTextMode_Notice_en', 'escaped');
      expect(() => customFieldForm('/api/studio/custom-field-create', invalid)).toThrow();
    }
    fields.delete('authorTextMode_Label_en');
    expect(() => customFieldForm('/api/studio/custom-field-create', fields)).toThrow();
  });
});
