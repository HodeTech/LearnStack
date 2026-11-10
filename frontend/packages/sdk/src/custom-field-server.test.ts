import { describe, expect, it, vi } from 'vitest';

import {
  createCustomFieldServerSdk,
  parseCustomFieldJson,
  parseCustomFieldNumber,
} from './custom-field-server';
const id = '019a154b-0000-7000-8000-000000000011';
const other = '019a154b-0000-7000-8000-000000000022';
const options = { version: '9007199254740993', idempotencyKey: 'independent-key' };
const response = (value: unknown, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(value), {
    headers: { 'content-type': 'application/json', ...headers },
  });
describe('custom-field numeric and hostile JSON boundary', () => {
  it.each([
    '9007199254740992',
    '-9007199254740992',
    '1e400',
    '1e-400',
    '-1e-400',
    '{"value":1,"\\u0076alue":2}',
    '{"constructor":1}',
    '[1,]',
  ])('refuses %s before any projection', (raw) =>
    expect(() => parseCustomFieldJson(raw)).toThrow(),
  );
  it.each(['9007199254740991', '-9007199254740991', '1.25e-200', '0e-400'])(
    'roundtrips supported %s',
    (raw) =>
      expect(parseCustomFieldJson(JSON.stringify(parseCustomFieldJson(raw)))).toEqual(
        parseCustomFieldJson(raw),
      ),
  );
  it('enforces actual UTF-8 bytes', () =>
    expect(() => parseCustomFieldJson(JSON.stringify('é'.repeat(2048)))).toThrow());
});
describe('closed custom-value transport', () => {
  it('sends exact PATCH, preserves ETag text and returns only safe receipt', async () => {
    const transport = vi.fn(async () =>
      response({ id, version: '9007199254740994' }, { ETag: '"9007199254740994"' }),
    );
    const sdk = createCustomFieldServerSdk(transport);
    const raw =
      '{"set":[{"conceptKey":"bounded-answer","definitionId":"' +
      other +
      '","schemaVersion":1,"envelope":{"value":9007199254740991},"consentEvidenceId":null}],"remove":[]}';
    const result = await sdk.setMembershipCustomFields(id, raw, options);
    expect(result.kind).toBe('success');
    expect(transport).toHaveBeenCalledWith(
      '/api/v1/identity/memberships/' + id + '/custom-fields',
      expect.objectContaining({
        method: 'PATCH',
        body: raw,
        headers: expect.objectContaining({ 'If-Match': '"9007199254740993"' }),
      }),
    );
  });
  it('does not normalize unsafe raw nested input before authoritative API admission', async () => {
    const transport = vi.fn();
    const sdk = createCustomFieldServerSdk(transport);
    const raw =
      '{"set":[{"conceptKey":"answer","definitionId":"' +
      other +
      '","schemaVersion":1,"envelope":{"value":{"child":1e-400}},"consentEvidenceId":null}],"remove":[]}';
    expect(await sdk.setOwnCustomFields(raw, options)).toEqual({ kind: 'invalid-request' });
    expect(transport).not.toHaveBeenCalled();
  });
  it('refuses replay receipts containing schema or values', async () => {
    const sdk = createCustomFieldServerSdk(async () =>
      response(
        { id, version: '2', schema: { value: 'canary' } },
        { ETag: '"2"', 'Idempotency-Replayed': 'true' },
      ),
    );
    expect(
      await sdk.setOwnCustomFields(
        { set: [], remove: [{ conceptKey: 'answer', definitionId: other, schemaVersion: 1 }] },
        options,
      ),
    ).toEqual({ kind: 'invalid-response', reason: 'invalid-success' });
  });
  it('keeps withdrawal scoped by owned membership query and explicit predecessor', async () => {
    const transport = vi.fn(async () =>
      response({ evidenceId: other, sequence: '2', isGranted: false }),
    );
    const sdk = createCustomFieldServerSdk(transport);
    expect(
      (
        await sdk.withdrawOwnFieldConsent(
          id,
          { evidenceId: other, expectedSequence: 1, expectedEvidenceId: other, confirmed: true },
          { idempotencyKey: 'withdraw-key' },
        )
      ).kind,
    ).toBe('success');
    expect(transport).toHaveBeenCalledWith(
      '/api/v1/tenancy/membership-field-consents/withdrawals?membershipId=' + id,
      expect.objectContaining({ method: 'POST' }),
    );
  });
  it('refuses malformed/oversized response before exposing data', async () => {
    const sdk = createCustomFieldServerSdk(
      async () =>
        new Response(' '.repeat(256 * 1024 + 1), {
          headers: { 'content-type': 'application/json' },
        }),
    );
    expect((await sdk.getOwnCustomFields()).kind).toBe('invalid-response');
  });
});

describe('typed integer numeric lexemes', () => {
  it.each(['1.00000000000000000001', '-1.00000000000000000001', '1e-999', '9007199254740992'])(
    'refuses integer precision loss %s',
    (raw) => expect(() => parseCustomFieldNumber(raw, true)).toThrow(),
  );
  it.each(['1.00', '1e2', '0', '-0'])('accepts mathematically integral %s', (raw) =>
    expect(parseCustomFieldNumber(raw, true)).toBe(Number(raw) === 0 ? 0 : Number(raw)),
  );
  it('retains binary64 number semantics for a fractional lexeme that rounds to an integer', () =>
    expect(parseCustomFieldNumber('1.00000000000000000001', false)).toBe(1));
});

describe('sensitive custom-value disclosure admission', () => {
  it.each(['Current', 'Retired'] as const)(
    'requires current granted evidence before exposing a %s value',
    async (state) => {
      const item = {
        conceptKey: 'answer',
        definitionId: other,
        schemaVersion: 1,
        state,
        isRequired: false,
        envelope: { value: true },
        consent: null,
        definition: {
          schema: {
            $schema: 'https://json-schema.org/draft/2020-12/schema',
            type: 'object',
            properties: { value: { type: 'boolean' } },
            required: ['value'],
            additionalProperties: false,
          },
          labels: { en: 'Answer' },
          piiCategory: 'Sensitive',
          sort: 0,
          purposeKey: 'purpose',
          notices: { en: 'Notice' },
          noticeHashes: { en: 'a'.repeat(64) },
          formLabels: null,
          status: 'Active',
        },
      };
      for (const consent of [
        null,
        { evidenceId: other, sequence: '2', isGranted: false },
        { evidenceId: other, sequence: '3', isGranted: true },
      ]) {
        const sdk = createCustomFieldServerSdk(async () =>
          response(
            { membershipId: id, version: '1', fields: [{ ...item, consent }] },
            { ETag: '"1"' },
          ),
        );
        const expected = consent?.isGranted ? 'success' : 'invalid-response';
        expect((await sdk.getOwnCustomFields()).kind).toBe(expected);
        expect((await sdk.getMembershipCustomFields(id)).kind).toBe(expected);
        const withheld = createCustomFieldServerSdk(async () =>
          response(
            {
              membershipId: id,
              version: '1',
              fields: [{ ...item, state: 'Withheld', envelope: null, consent }],
            },
            { ETag: '"1"' },
          ),
        );
        expect((await withheld.getOwnCustomFields()).kind).toBe('success');
      }
    },
  );
});
