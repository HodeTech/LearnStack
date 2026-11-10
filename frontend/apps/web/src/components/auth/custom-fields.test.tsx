import {
  customFieldChoiceToken,
  customFieldTextMode,
  decodeCustomFieldText,
  encodeCustomFieldText,
} from '@learnstack/sdk';
import type {
  CustomFieldDefinition,
  CustomFieldPatch,
  MembershipCustomFields,
} from '@learnstack/sdk/administration-server';
import { fireEvent, render, screen } from '@testing-library/react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';

import enAdmin from '@/i18n/messages/en/admin.json';
import trAdmin from '@/i18n/messages/tr/admin.json';
import { customFieldForm } from '@/server/auth/custom-field-forms';
import { administrationSnapshot, uiMemberId } from '@/test/administration-ui-fixture';

import { CustomFieldBuilder } from './custom-field-builder';
import { FieldAnswer, FieldControls, StaticFieldControls } from './custom-field-controls';
import type { FieldControlsProps } from './custom-field-controls';
import { CustomFieldNativeForm } from './custom-field-native-form';
import { renderStaticCustomFieldControls } from './custom-field-native-html';
import { customFieldMutationContent } from './custom-field-recovery';
import {
  envelopeSchema,
  readFieldSchema,
  validFieldKey,
  enabledFieldControlLabels,
} from './custom-field-schema';
import { CustomFieldDetails } from './custom-fields';
import { MemberCustomFields, OwnConsentHistory } from './member-custom-fields';

const definitionId = '019a154b-0000-7000-8000-000000000099';
const evidenceId = '019a154b-0000-7000-8000-000000000088';
const scalar = envelopeSchema({ type: 'string', minLength: 1, maxLength: 32 });
function snapshot() {
  return administrationSnapshot(
    { kind: 'profile', canWrite: true },
    {
      formKeys: Array.from(
        { length: 180 },
        (_, index) => String(index).padStart(3, '0') + 'K'.repeat(40),
      ),
    },
  );
}
function field(
  overrides: Partial<MembershipCustomFields['fields'][number]> = {},
): MembershipCustomFields['fields'][number] {
  return {
    conceptKey: 'retained',
    definitionId,
    schemaVersion: 1,
    state: 'Current',
    isRequired: false,
    envelope: { value: 'SAFE ANSWER' },
    consent: null,
    definition: {
      schema: scalar,
      labels: { en: '<img src=x onerror=alert(1)>', tr: 'Görünen etiket' },
      piiCategory: 'None',
      sort: 0,
      purposeKey: null,
      notices: null,
      noticeHashes: null,
      formLabels: null,
      status: 'Active',
    },
    ...overrides,
  };
}
const fields = (items: MembershipCustomFields['fields']): MembershipCustomFields => ({
  membershipId: uiMemberId,
  version: '9223372036854775807',
  fields: items,
});

function enhancedSubmission(form: HTMLFormElement): FormData {
  fireEvent.submit(form);
  const data = new FormData(form);
  const event = new Event('formdata');
  Object.defineProperty(event, 'formData', { value: data });
  form.dispatchEvent(event);
  return data;
}

describe('finite custom-field UI', () => {
  it.each<[string, Partial<FieldControlsProps> & Pick<FieldControlsProps, 'schema'>]>([
    [
      'string',
      { schema: { type: 'string', minLength: 1, maxLength: 32 }, value: '<img src=x>&"\'' },
    ],
    ['boolean', { schema: { type: 'boolean' }, value: false }],
    [
      'integer raw lexeme',
      {
        schema: { type: 'integer', minimum: -9007199254740991, maximum: 9007199254740991 },
        submitted: { answer_0_root: '9007199254740993' },
      },
    ],
    [
      'number raw lexeme',
      {
        schema: { type: 'number', minimum: 0, maximum: 1 },
        submitted: { answer_0_root: '1e-400' },
      },
    ],
    [
      'enum',
      {
        schema: { type: 'string', minLength: 1, maxLength: 16, enum: ['one', 'two'] },
        value: 'two',
        formLabels: [
          {
            location: '/',
            labels: null,
            choices: [{ en: 'First <choice>' }, { tr: 'İkinci & seçenek' }] as Readonly<
              Record<string, string>
            >[],
          },
        ],
      },
    ],
    [
      'nested optional list',
      {
        schema: {
          type: 'object',
          properties: {
            'a-group': {
              type: 'array',
              minItems: 0,
              maxItems: 2,
              items: { type: 'number', minimum: -5, maximum: 5 },
            },
            'z-flag': { type: 'boolean' },
          },
          required: ['z-flag'],
          additionalProperties: false,
        },
        submitted: {
          presence_0_root_0: 'off',
          count_0_root_0: '1',
          answer_0_root_0_0: '1e-400',
          answer_0_root_0_1: '',
          answer_0_root_1: 'false',
        },
        formLabels: [
          { location: '/0', labels: { tr: 'Liste <başlığı>' }, choices: null },
          { location: '/0/0', labels: { en: 'List item' }, choices: null },
          { location: '/1', labels: { en: 'Required flag' }, choices: null },
        ],
      },
    ],
  ])('keeps native escaped HTML equivalent to React static controls for %s', (_kind, input) => {
    const props: FieldControlsProps = {
      ...input,
      labels: { en: '<svg onload=alert(1)> authored label' },
      locale: 'tr',
      defaultLocale: 'en',
      copy: trAdmin.custom_fields,
      prefix: '0',
    };
    function semantics(html: string) {
      const document = new DOMParser().parseFromString(html, 'text/html');
      const attributes = (element: Element) =>
        [...element.attributes]
          .map((attribute) => [attribute.name, attribute.value])
          .sort(([a], [b]) => a!.localeCompare(b!));
      return {
        controls: [
          ...document.querySelectorAll<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>(
            'input,select,textarea',
          ),
        ].map((element) => ({
          tag: element.tagName,
          attributes: attributes(element),
          value: element.value,
          options:
            element instanceof HTMLSelectElement
              ? [...element.options].map((option) => ({
                  attributes: attributes(option),
                  text: option.textContent,
                  selected: option.selected,
                }))
              : [],
        })),
        labels: [...document.querySelectorAll('label,legend,[lang],p[id],summary')].map(
          (element) => ({
            tag: element.tagName,
            attributes: attributes(element),
            text: element.textContent?.replace(/\s+/g, ' ').trim(),
          }),
        ),
        details: [...document.querySelectorAll('details')].map((element) => element.open),
      };
    }
    const native = renderStaticCustomFieldControls(props);
    const react = renderToStaticMarkup(<StaticFieldControls {...props} />);
    expect(JSON.stringify(semantics(native)) === JSON.stringify(semantics(react))).toBe(true);
    const document = new DOMParser().parseFromString(native, 'text/html');
    expect(document.querySelector('img,svg,script')).toBeNull();
    expect(document.querySelector('[name="controlSchema"]')).toBeNull();
  });

  it('refuses ignored schema keywords, third container levels and unsafe numeric bounds before rendering', () => {
    expect(readFieldSchema(scalar)).not.toBeNull();
    expect(validFieldKey('1-valid-key')).toBe(true);
    expect(validFieldKey('invalid_key')).toBe(false);
    expect(validFieldKey('valid-looking\n')).toBe(false);
    for (const child of [
      { type: 'string', minLength: 1, maxLength: 32, format: 'date' },
      { type: 'string', minLength: 1, maxLength: 32, default: 'secret' },
      { type: 'number', minimum: 0, maximum: 9007199254740992 },
      { type: 'null' },
      { $ref: 'https://example.test' },
      {
        type: 'array',
        minItems: 0,
        maxItems: 1,
        items: {
          type: 'array',
          minItems: 0,
          maxItems: 1,
          items: {
            type: 'object',
            properties: { leaf: { type: 'boolean' } },
            required: [],
            additionalProperties: false,
          },
        },
      },
    ])
      expect(readFieldSchema({ ...scalar, properties: { value: child } })).toBeNull();
  });

  it('renders numeric answers as text lexemes, labels every control, and marks requiredness without blocking independent removal', () => {
    const html = renderToStaticMarkup(
      <FieldControls
        schema={{ type: 'integer', minimum: -9007199254740991, maximum: 9007199254740991 }}
        labels={{ en: 'Count' }}
        locale="en"
        defaultLocale="en"
        copy={enAdmin.custom_fields}
        prefix="0"
      />,
    );
    const document = new DOMParser().parseFromString(html, 'text/html');
    const input = document.querySelector('input')!;
    expect(input.type).toBe('text');
    expect(input.name).toBe('answer_0_root');
    expect(input.required).toBe(false);
    expect(input.getAttribute('aria-required')).toBe('true');
    expect(document.querySelector('label')?.htmlFor).toBe(input.id);
    expect(document.getElementById(input.getAttribute('aria-describedby')!)).not.toBeNull();
  });

  it('uses structural ordinal labels even when an earlier optional object property is absent', () => {
    const html = renderToStaticMarkup(
      <FieldAnswer
        schema={{
          type: 'object',
          properties: { alpha: { type: 'boolean' }, zeta: { type: 'boolean' } },
          required: [],
          additionalProperties: false,
        }}
        value={{ zeta: true }}
        labels={{ en: 'Group' }}
        formLabels={[
          { location: '/0', labels: { en: 'First' } },
          { location: '/1', labels: { tr: 'İkinci' } },
        ]}
        locale="en"
        defaultLocale="tr"
        copy={enAdmin.custom_fields}
      />,
    );
    expect(html).toContain('lang="tr">İkinci');
    expect(html).not.toContain('>First<');
  });

  it.each([
    ['en', enAdmin.custom_fields],
    ['tr', trAdmin.custom_fields],
  ] as const)(
    'escapes hostile labels and keeps removal usable in an incomplete form (%s)',
    (locale, copy) => {
      const data = fields([
        field(),
        field({
          definitionId: '019a154b-0000-7000-8000-000000000098',
          conceptKey: 'required',
          state: 'Missing',
          isRequired: true,
          envelope: null,
        }),
      ]);
      const html = renderToStaticMarkup(
        <MemberCustomFields
          snapshot={snapshot()}
          fields={data}
          own={false}
          canWrite
          locale={locale}
          copy={copy}
        />,
      );
      const document = new DOMParser().parseFromString(html, 'text/html');
      expect(document.querySelector('img')).toBeNull();
      const removal = [...document.querySelectorAll('form')].find((form) => {
        const payload = form.querySelector<HTMLInputElement>('input[name="payload"]');
        return payload && JSON.parse(payload.value).remove.length === 1;
      })!;
      expect(removal).toBeDefined();
      expect(removal.querySelector('[name^="answer_"]')).toBeNull();
      expect(removal.querySelector<HTMLInputElement>('input[name="version"]')?.value).toBe(
        '9223372036854775807',
      );
      expect(removal.querySelector('button')?.textContent).toBe(copy.remove);
      expect(document.querySelector('[name="answer_0_root"]')).not.toBeNull();
    },
  );

  it('never serializes a withheld envelope and never lets an administrator grant another person consent', () => {
    const item = field({
      state: 'Withheld',
      envelope: { value: 'CANARY NEVER DISCLOSE' },
      definition: {
        ...field().definition,
        piiCategory: 'Sensitive',
        purposeKey: 'purpose',
        notices: { en: 'Exact notice' },
        noticeHashes: { en: 'a'.repeat(64) },
      },
    });
    const html = renderToStaticMarkup(
      <MemberCustomFields
        snapshot={snapshot()}
        fields={fields([item])}
        own={false}
        canWrite
        selectedFieldId={definitionId}
        locale="en"
        copy={enAdmin.custom_fields}
      />,
    );
    expect(html).not.toContain('CANARY NEVER DISCLOSE');
    expect(html).not.toContain('action="/api/account/consent-grant"');
    expect(html).toContain(enAdmin.custom_fields.admin_consent);
  });

  it('keeps learner consent separate from profile-write permission and binds the authored fallback notice', () => {
    const item = field({
      state: 'Missing',
      envelope: null,
      definition: {
        ...field().definition,
        piiCategory: 'Sensitive',
        purposeKey: 'purpose',
        notices: { en: 'Exact notice' },
        noticeHashes: { en: 'a'.repeat(64) },
      },
    });
    const html = renderToStaticMarkup(
      <MemberCustomFields
        snapshot={snapshot()}
        fields={fields([item])}
        own
        canWrite={false}
        locale="tr"
        copy={trAdmin.custom_fields}
      />,
    );
    const document = new DOMParser().parseFromString(html, 'text/html');
    const grant = document.querySelector('form[action="/api/account/consent-grant"]')!;
    expect(grant).not.toBeNull();
    expect(grant.querySelector<HTMLInputElement>('[name="noticeLocale"]')?.value).toBe('en');
    expect(document.querySelector('p[lang="en"]')?.textContent).toBe('Exact notice');
    expect(document.querySelector('form[action="/api/account/custom-fields"]')).toBeNull();
    expect(document.querySelector('a[href="/account/consents"]')).not.toBeNull();
  });

  it('renders withdrawal from retained history without any active definition or stored value', () => {
    const html = renderToStaticMarkup(
      <OwnConsentHistory
        snapshot={snapshot()}
        locale="en"
        copy={enAdmin.custom_fields}
        history={{
          items: [
            {
              evidenceId,
              sequence: '9223372036854775807',
              isGranted: true,
              membershipId: uiMemberId,
              definitionId,
              definitionVersion: 1,
              purposeKey: 'purpose',
              noticeLocale: 'tr',
              noticeHash: 'a'.repeat(64),
              noticeText: 'Saklanan bildirim',
              labels: { en: 'Historical field' },
            },
          ],
          pageInfo: { nextCursor: null, previousCursor: null, hasNext: false, hasPrevious: false },
        }}
      />,
    );
    expect(html).toContain('action="/api/account/consent-withdraw"');
    expect(html).toContain('value="9223372036854775807"');
    expect(html).toContain('lang="tr">Saklanan bildirim');
  });

  it('keeps rejected numeric lexemes editable only in transient recovery and posts no schema', () => {
    const html = customFieldMutationContent(
      {
        kind: 'mutation_error',
        action: '/api/account/custom-fields',
        destination: '/portal/profile',
        fields: [
          ['csrfToken', 'A'.repeat(43)],
          ['idempotencyKey', 'K'.repeat(43)],
          ['version', '7'],
          [
            'payload',
            JSON.stringify({
              set: [
                {
                  conceptKey: 'count',
                  definitionId,
                  schemaVersion: 1,
                  envelope: { value: null },
                  consentEvidenceId: null,
                },
              ],
              remove: [],
            }),
          ],
          ['answer_0_root', '9007199254740993'],
        ],
        reason: 'validation',
        retryable: false,
        customFieldControls: [
          {
            index: 0,
            schema: envelopeSchema({
              type: 'integer',
              minimum: -9007199254740991,
              maximum: 9007199254740991,
            }),
            labels: { en: 'Exact count' },
            formLabels: null,
          },
        ],
      },
      'en',
      snapshot().site,
    )!;
    const document = new DOMParser().parseFromString(html, 'text/html');
    expect(document.querySelector<HTMLInputElement>('[name="answer_0_root"]')?.value).toBe(
      '9007199254740993',
    );
    expect(document.querySelector('[name="controlSchema"]')).toBeNull();
    expect(document.querySelector('label')?.textContent).toContain('Exact count');
    expect(document.querySelector('[role="alert"]')?.textContent).not.toContain('9007199254740993');
  });

  it('offers reconsent for an exact retained Deprecated pin, independently of profile-write permission', () => {
    const retained = field({
      state: 'Withheld',
      envelope: null,
      definition: {
        ...field().definition,
        status: 'Deprecated',
        piiCategory: 'Sensitive',
        purposeKey: 'retained-purpose',
        notices: { en: 'Original retained notice' },
        noticeHashes: { en: 'b'.repeat(64) },
      },
    });
    const html = renderToStaticMarkup(
      <MemberCustomFields
        snapshot={snapshot()}
        fields={fields([retained])}
        own
        canWrite={false}
        locale="en"
        copy={enAdmin.custom_fields}
      />,
    );
    const document = new DOMParser().parseFromString(html, 'text/html');
    const grant = document.querySelector('form[action="/api/account/consent-grant"]');
    expect(grant).not.toBeNull();
    expect(grant?.querySelector<HTMLInputElement>('[name="definitionId"]')?.value).toBe(
      definitionId,
    );
    expect(grant?.querySelector<HTMLInputElement>('[name="expectedNoticeHash"]')?.value).toBe(
      'b'.repeat(64),
    );
    expect(document.querySelector('form[action="/api/account/custom-fields"]')).toBeNull();
    const missingHtml = renderToStaticMarkup(
      <MemberCustomFields
        snapshot={snapshot()}
        fields={fields([{ ...retained, state: 'Missing' }])}
        own
        canWrite={false}
        locale="en"
        copy={enAdmin.custom_fields}
      />,
    );
    expect(missingHtml).not.toContain('action="/api/account/consent-grant"');
  });

  it('submits only actual list items and present optional branches', () => {
    const { container } = render(
      <form>
        <FieldControls
          schema={{
            type: 'object',
            properties: {
              nested: {
                type: 'array',
                minItems: 0,
                maxItems: 10,
                items: { type: 'array', minItems: 0, maxItems: 10, items: { type: 'boolean' } },
              },
            },
            required: [],
            additionalProperties: false,
          }}
          labels={{ en: 'Group' }}
          formLabels={[
            { location: '/0', labels: { en: 'Optional list' } },
            { location: '/0/0', labels: { en: 'Inner list' } },
            { location: '/0/0/0', labels: { en: 'Answer' } },
          ]}
          locale="en"
          defaultLocale="en"
          copy={enAdmin.custom_fields}
          prefix="0"
        />
      </form>,
    );
    const form = container.querySelector('form')!;
    expect([...new FormData(form).keys()]).toEqual(['presence_0_root_0']);
    fireEvent.change(form.querySelector('[name="presence_0_root_0"]')!, {
      target: { value: 'on' },
    });
    expect([...new FormData(form).keys()]).toEqual(['presence_0_root_0', 'count_0_root_0']);
    fireEvent.change(form.querySelector('[name="count_0_root_0"]')!, { target: { value: '2' } });
    fireEvent.change(form.querySelector('[name="count_0_root_0_0"]')!, { target: { value: '1' } });
    expect(form.querySelectorAll('[name^="answer_"]')).toHaveLength(1);
    expect(form.querySelector('[name="answer_0_root_0_0_0"]')).not.toBeNull();
    fireEvent.change(form.querySelector('[name="count_0_root_0"]')!, { target: { value: '0' } });
    expect(form.querySelector('[name^="answer_"]')).toBeNull();
    expect(form.querySelector('[name="count_0_root_0_0"]')).toBeNull();
    fireEvent.change(form.querySelector('[name="presence_0_root_0"]')!, {
      target: { value: 'off' },
    });
    expect([...new FormData(form).keys()]).toEqual(['presence_0_root_0']);
  });

  it('renders native typed list controls with distinct IDs and ignores only unused declared items', () => {
    const schema = envelopeSchema({
      type: 'array',
      minItems: 0,
      maxItems: 3,
      items: { type: 'boolean' },
    });
    const item = field({
      state: 'Missing',
      envelope: null,
      definition: {
        ...field().definition,
        schema,
        formLabels: [{ location: '/0', labels: { en: 'List answer' }, choices: null }],
      },
    });
    const data = fields([item]);
    const patch = {
      set: [
        {
          conceptKey: item.conceptKey,
          definitionId: item.definitionId,
          schemaVersion: item.schemaVersion,
          envelope: { value: null },
          consentEvidenceId: null,
        },
      ],
      remove: [],
    };
    const { container } = render(
      <>
        <form>
          <FieldControls
            schema={schema.properties.value}
            labels={item.definition.labels}
            formLabels={item.definition.formLabels}
            locale="en"
            defaultLocale="en"
            copy={enAdmin.custom_fields}
            prefix="0"
          />
        </form>
        <form id="native-list">
          <input type="hidden" name="csrfToken" value={'A'.repeat(43)} />
          <input type="hidden" name="idempotencyKey" value={'K'.repeat(43)} />
          <input type="hidden" name="version" value={data.version} />
          <input type="hidden" name="payload" value={JSON.stringify(patch)} />
          <StaticFieldControls
            schema={schema.properties.value}
            labels={item.definition.labels}
            formLabels={item.definition.formLabels}
            locale="en"
            defaultLocale="en"
            copy={enAdmin.custom_fields}
            prefix="0"
          />
        </form>
      </>,
    );
    const ids = [...container.querySelectorAll('[id]')].map((element) => element.id);
    expect(new Set(ids).size).toBe(ids.length);
    const form = container.querySelector<HTMLFormElement>('#native-list')!;
    expect(form.querySelector('textarea')).toBeNull();
    expect(form.querySelectorAll('[name^="answer_"]')).toHaveLength(3);
    expect(form.querySelector('[name="count_0_root"]')?.id).toBe('ns-0-root-count');
    fireEvent.change(form.querySelector('[name="count_0_root"]')!, { target: { value: '2' } });
    fireEvent.change(form.querySelector('[name="answer_0_root_0"]')!, {
      target: { value: 'true' },
    });
    fireEvent.change(form.querySelector('[name="answer_0_root_1"]')!, {
      target: { value: 'false' },
    });
    const encoded = new URLSearchParams(
      [...new FormData(form)].map(([name, value]) => [name, String(value)]),
    );
    expect(encoded.get('answer_0_root_2')).toBe('');
    expect(new TextEncoder().encode(encoded.toString()).length).toBeLessThanOrEqual(16384);
    const parsed = customFieldForm('/api/account/custom-fields', encoded, data);
    if (parsed.operation.kind !== 'setOwnCustomFields') throw new Error('Wrong form operation');
    expect(JSON.parse(parsed.operation.patch as string).set[0].envelope.value).toEqual([
      true,
      false,
    ]);
  });

  it('fits the maximum finite numeric control tree and long integral lexemes in the unchanged native bound', () => {
    const schema = envelopeSchema({
      type: 'array',
      minItems: 10,
      maxItems: 10,
      items: {
        type: 'array',
        minItems: 10,
        maxItems: 10,
        items: { type: 'integer', minimum: 0, maximum: 1 },
      },
    });
    const item = field({
      state: 'Missing',
      envelope: null,
      definition: {
        ...field().definition,
        schema,
        formLabels: [
          { location: '/0', labels: { en: 'Row' }, choices: null },
          { location: '/0/0', labels: { en: 'Count' }, choices: null },
        ],
      },
    });
    const data = fields([item]);
    const patch = {
      set: [
        {
          conceptKey: item.conceptKey,
          definitionId: item.definitionId,
          schemaVersion: item.schemaVersion,
          envelope: { value: null },
          consentEvidenceId: null,
        },
      ],
      remove: [],
    };
    const { container } = render(
      <form>
        <input type="hidden" name="csrfToken" value={'A'.repeat(43)} />
        <input type="hidden" name="idempotencyKey" value={'K'.repeat(43)} />
        <input type="hidden" name="version" value={data.version} />
        <input type="hidden" name="payload" value={JSON.stringify(patch)} />
        <StaticFieldControls
          schema={schema.properties.value}
          labels={item.definition.labels}
          formLabels={item.definition.formLabels}
          locale="en"
          defaultLocale="en"
          copy={enAdmin.custom_fields}
          prefix="0"
        />
      </form>,
    );
    const form = container.querySelector('form')!;
    for (const input of form.querySelectorAll<HTMLInputElement>('input[name^="answer_"]'))
      input.value = '1e+' + '0'.repeat(125);
    expect(form.querySelectorAll('[name^="answer_"]')).toHaveLength(100);
    const encoded = new URLSearchParams(
      [...new FormData(form)].map(([name, value]) => [name, String(value)]),
    );
    expect(new TextEncoder().encode(encoded.toString()).length).toBeLessThanOrEqual(16384);
    const parsed = customFieldForm('/api/account/custom-fields', encoded, data);
    if (parsed.operation.kind !== 'setOwnCustomFields') throw new Error('Wrong form operation');
    expect(new TextEncoder().encode(String(parsed.operation.patch)).length).toBeLessThanOrEqual(
      4096,
    );
    expect(JSON.parse(parsed.operation.patch as string).set[0].envelope.value).toEqual(
      Array.from({ length: 10 }, () => Array.from({ length: 10 }, () => 1)),
    );
  });

  it('collects missing required answers before offering a separate optional edit', () => {
    const optional = field();
    const required = field({
      definitionId: '019a154b-0000-7000-8000-000000000098',
      conceptKey: 'required',
      state: 'Missing',
      isRequired: true,
      envelope: null,
    });
    const html = renderToStaticMarkup(
      <MemberCustomFields
        snapshot={snapshot()}
        fields={fields([optional, required])}
        canWrite
        own
        selectedFieldId={optional.definitionId}
        locale="en"
        copy={enAdmin.custom_fields}
      />,
    );
    const document = new DOMParser().parseFromString(html, 'text/html');
    const form = [...document.querySelectorAll('form')].find((candidate) =>
      candidate.querySelector('[name^="answer_"]'),
    )!;
    const patch = JSON.parse(form.querySelector<HTMLInputElement>('[name="payload"]')!.value);
    expect(patch.set.map((operation: { definitionId: string }) => operation.definitionId)).toEqual([
      required.definitionId,
    ]);
    expect(document.body.textContent).toContain(enAdmin.custom_fields.required_first);
    expect(document.querySelector('a[href*="?field="]')).toBeNull();
  });

  it('encodes a large valid nested answer as one bounded payload instead of hundreds of input names', () => {
    const schema = envelopeSchema({
      type: 'array',
      minItems: 10,
      maxItems: 10,
      items: {
        type: 'array',
        minItems: 10,
        maxItems: 10,
        items: { type: 'integer', minimum: 0, maximum: 1 },
      },
    });
    const set = Array.from({ length: 5 }, (_, index) => ({
      conceptKey: 'field-' + index,
      definitionId: '019a154b-0000-7000-8000-' + String(index).padStart(12, '0'),
      schemaVersion: 1,
      envelope: { value: null },
      consentEvidenceId: null,
    }));
    const patch: CustomFieldPatch = { set, remove: [] };
    const value = Array.from({ length: 10 }, () => Array.from({ length: 10 }, () => 1));
    const { container } = render(
      <CustomFieldNativeForm
        action="/api/account/custom-fields"
        patch={patch}
        schemata={set.map(() => schema)}
        complex
        copy={enAdmin.custom_fields}
      >
        <input type="hidden" name="csrfToken" value={'A'.repeat(43)} />
        <input type="hidden" name="idempotencyKey" value={'K'.repeat(43)} />
        <input type="hidden" name="version" value="7" />
        <input type="hidden" name="payload" value={JSON.stringify(patch)} />
        {set.map((operation, index) => (
          <FieldControls
            key={operation.definitionId}
            schema={schema.properties.value}
            labels={{ en: 'Answer ' + index }}
            formLabels={[
              { location: '/0', labels: { en: 'Row' } },
              { location: '/0/0', labels: { en: 'Choice' } },
            ]}
            locale="en"
            defaultLocale="en"
            copy={enAdmin.custom_fields}
            prefix={String(index)}
            value={value}
          />
        ))}
        <button type="submit">Save</button>
      </CustomFieldNativeForm>,
    );
    const form = container.querySelector('form')!;
    expect(form.querySelectorAll('[name^="answer_"]')).toHaveLength(500);
    for (const input of form.querySelectorAll<HTMLInputElement>('input[name^="answer_"]'))
      input.value = '1.0000000000000000000000000000000000';
    const before = new URLSearchParams(
      [...new FormData(form)].map(([name, value]) => [name, String(value)]),
    );
    expect(new TextEncoder().encode(before.toString()).length).toBeGreaterThan(16384);
    const submitted = enhancedSubmission(form);
    expect(
      [...submitted.keys()].some((name) => /^(?:answer|count|presence|textmode)_/.test(name)),
    ).toBe(false);
    expect(form.querySelectorAll('[name^="answer_"]')).toHaveLength(500);
    const payload = String(submitted.get('payload'));
    expect(new TextEncoder().encode(payload).length).toBeLessThanOrEqual(4096);
    expect(JSON.parse(payload).set[0].envelope.value).toEqual(value);
    const after = new URLSearchParams([...submitted].map(([name, value]) => [name, String(value)]));
    expect(new TextEncoder().encode(after.toString()).length).toBeLessThanOrEqual(16384);
  });

  it('refuses fractional integer bound lexemes before the builder can round them into integers', () => {
    render(
      <CustomFieldBuilder
        csrfToken={'A'.repeat(43)}
        formKey={'K'.repeat(43)}
        locales={['en']}
        defaultLocale="en"
        locale="en"
        copy={enAdmin.custom_fields}
      />,
    );
    fireEvent.change(screen.getByLabelText(enAdmin.custom_fields.key), {
      target: { value: 'whole-count' },
    });
    fireEvent.change(screen.getByLabelText('Field label (en)'), {
      target: { value: 'Whole count' },
    });
    fireEvent.change(screen.getByLabelText(enAdmin.custom_fields.category), {
      target: { value: 'None' },
    });
    fireEvent.change(screen.getByLabelText(enAdmin.custom_fields.type), {
      target: { value: 'integer' },
    });
    fireEvent.change(screen.getByLabelText(enAdmin.custom_fields.min), {
      target: { value: '1.0000000000000000001' },
    });
    const form = screen
      .getByRole('button', { name: enAdmin.custom_fields.create })
      .closest('form')!;
    expect(form.querySelector<HTMLInputElement>('[name="payload"]')?.value).toBe('');
    fireEvent.submit(form);
    expect(screen.getByRole('alert')).toHaveTextContent(enAdmin.custom_fields.errors);
  });

  it('saves only visible enabled translations in a successor and preserves the published source', () => {
    const initial: CustomFieldDefinition = {
      definitionId,
      targetEntity: 'Membership',
      key: 'translated-choice',
      schemaVersion: 1,
      version: '0',
      status: 'Active',
      activeRevision: { id: definitionId, version: '0' },
      isDiscarded: false,
      maximumValueBytes: 128,
      schema: envelopeSchema({
        type: 'object',
        properties: {
          choice: { type: 'string', minLength: 1, maxLength: 16, enum: ['first', 'second'] },
        },
        required: ['choice'],
        additionalProperties: false,
      }),
      labels: { en: 'Original', tr: 'Özgün' },
      piiCategory: 'Sensitive',
      isRequired: false,
      sort: 0,
      purposeKey: 'purpose',
      notices: { en: 'Original notice', tr: 'Özgün bildirim' },
      formLabels: [
        {
          location: '/0',
          labels: { en: 'Choice', tr: 'Seçim' },
          choices: [
            { en: 'First', tr: 'İlk' },
            { en: 'Second', tr: 'İkinci' },
          ],
        },
      ],
    };
    const published = JSON.stringify(initial);
    const { container } = render(
      <CustomFieldBuilder
        initial={initial}
        successor
        csrfToken={'A'.repeat(43)}
        formKey={'K'.repeat(43)}
        locales={['en']}
        defaultLocale="en"
        locale="en"
        copy={enAdmin.custom_fields}
      />,
    );
    expect(screen.getByText(enAdmin.custom_fields.disabled_translations_hint)).toBeVisible();
    const payload = JSON.parse(
      container.querySelector<HTMLInputElement>('input[name="payload"]')!.value,
    );
    expect(payload.labels).toEqual({ en: 'Original' });
    expect(payload.notices).toEqual({ en: 'Original notice' });
    expect(payload.formLabels).toEqual([
      { location: '/0', labels: { en: 'Choice' }, choices: [{ en: 'First' }, { en: 'Second' }] },
    ]);
    expect(enabledFieldControlLabels(initial.formLabels, ['en'])).toEqual(payload.formLabels);
    expect(enabledFieldControlLabels(initial.formLabels, ['fr'])).toEqual([
      { location: '/0', labels: {}, choices: [{}, {}] },
    ]);
    expect(JSON.stringify(initial)).toBe(published);
  });

  it('requires a newly authored label and notice when the current default language has changed', () => {
    const initial: CustomFieldDefinition = {
      definitionId,
      targetEntity: 'Membership',
      key: 'changed-default',
      schemaVersion: 1,
      version: '0',
      status: 'Active',
      activeRevision: { id: definitionId, version: '0' },
      isDiscarded: false,
      maximumValueBytes: 128,
      schema: scalar,
      labels: { en: 'Original English label' },
      piiCategory: 'Sensitive',
      isRequired: false,
      sort: 0,
      purposeKey: 'purpose',
      notices: { en: 'Original English notice' },
      formLabels: null,
    };
    const { container } = render(
      <CustomFieldBuilder
        initial={initial}
        successor
        csrfToken={'A'.repeat(43)}
        formKey={'K'.repeat(43)}
        locales={['tr']}
        defaultLocale="tr"
        locale="tr"
        copy={trAdmin.custom_fields}
      />,
    );
    const label = container.querySelector<HTMLTextAreaElement>('#definition-control-label-tr')!;
    const notice = container.querySelector<HTMLTextAreaElement>('#definition-notice-tr')!;
    expect(label.value).toBe('');
    expect(notice.value).toBe('');
    expect(container.querySelector<HTMLInputElement>('input[name="payload"]')!.value).toBe('');
    fireEvent.change(label, { target: { value: 'Yeni etiket' } });
    expect(container.querySelector<HTMLInputElement>('input[name="payload"]')!.value).toBe('');
    fireEvent.change(notice, { target: { value: 'Yeni bildirim' } });
    const payload = JSON.parse(
      container.querySelector<HTMLInputElement>('input[name="payload"]')!.value,
    );
    expect(payload.labels).toEqual({ tr: 'Yeni etiket' });
    expect(payload.notices).toEqual({ tr: 'Yeni bildirim' });
    expect(initial.labels).toEqual({ en: 'Original English label' });
  });

  it('keeps the authored historical label language after a default-language change', () => {
    const old = field({
      state: 'Retired',
      envelope: { value: true },
      definition: {
        ...field().definition,
        status: 'Deprecated',
        schema: envelopeSchema({ type: 'boolean' }),
        labels: { en: 'Historical English label' },
      },
    });
    const prepared = snapshot();
    const html = renderToStaticMarkup(
      <MemberCustomFields
        snapshot={{
          ...prepared,
          site: { ...prepared.site, defaultLocale: 'tr', enabledLocales: ['tr'] },
        }}
        fields={fields([old])}
        canWrite={false}
        own={false}
        locale="tr"
        copy={trAdmin.custom_fields}
      />,
    );
    const document = new DOMParser().parseFromString(html, 'text/html');
    expect(document.querySelector('h3[lang="en"]')?.textContent).toBe('Historical English label');
    expect(document.querySelector('li > div[lang="tr"]')?.textContent).toBe(
      trAdmin.custom_fields.yes,
    );
  });

  it.each(['normal text', 'a\nb', 'a\rb', 'a\r\nb', 'a\r\nb\nc\rd', '\nstart\n', 'a\\b\\n'])(
    'preserves exact admitted string line endings across enhanced, native and recovery controls',
    (value) => {
      const schema = envelopeSchema({ type: 'string', minLength: 1, maxLength: 32 });
      const item = field({ definition: { ...field().definition, schema }, envelope: { value } });
      const data = fields([item]);
      const patch = {
        set: [
          {
            conceptKey: item.conceptKey,
            definitionId: item.definitionId,
            schemaVersion: item.schemaVersion,
            envelope: { value: null },
            consentEvidenceId: null,
          },
        ],
        remove: [],
      };
      const proof = (
        <>
          <input type="hidden" name="csrfToken" value={'A'.repeat(43)} />
          <input type="hidden" name="idempotencyKey" value={'K'.repeat(43)} />
          <input type="hidden" name="version" value={data.version} />
          <input type="hidden" name="payload" value={JSON.stringify(patch)} />
        </>
      );
      const { container } = render(
        <>
          <CustomFieldNativeForm
            action="/api/account/custom-fields"
            patch={patch}
            schemata={[schema]}
            complex={false}
            copy={enAdmin.custom_fields}
          >
            {proof}
            <FieldControls
              schema={schema.properties.value}
              labels={item.definition.labels}
              locale="en"
              defaultLocale="en"
              copy={enAdmin.custom_fields}
              prefix="0"
              value={value}
            />
            <button type="submit">Save</button>
          </CustomFieldNativeForm>
          <form id="native-text">
            {proof}
            <StaticFieldControls
              schema={schema.properties.value}
              labels={item.definition.labels}
              locale="en"
              defaultLocale="en"
              copy={enAdmin.custom_fields}
              prefix="0"
              value={value}
            />
          </form>
        </>,
      );
      const script = container.querySelector('form')!;
      const serialized = enhancedSubmission(script);
      expect(JSON.parse(String(serialized.get('payload'))).set[0].envelope.value === value).toBe(
        true,
      );
      expect(script.querySelector('[name="answer_0_root"]')).not.toBeNull();
      const native = container.querySelector<HTMLFormElement>('#native-text')!;
      const nativeData = new URLSearchParams(
        [...new FormData(native)].map(([name, value]) => [name, String(value)]),
      );
      nativeData.set('answer_0_root', nativeData.get('answer_0_root')!.replace(/\n/g, '\r\n'));
      const parsed = customFieldForm('/api/account/custom-fields', nativeData, data);
      if (parsed.operation.kind !== 'setOwnCustomFields') throw new Error('Wrong form operation');
      expect(JSON.parse(String(parsed.operation.patch)).set[0].envelope.value === value).toBe(true);
      const html = renderStaticCustomFieldControls({
        schema: schema.properties.value,
        labels: item.definition.labels,
        locale: 'en',
        defaultLocale: 'en',
        copy: enAdmin.custom_fields,
        prefix: '0',
        value,
      });
      const document = new DOMParser().parseFromString(html, 'text/html');
      const source = document.querySelector<HTMLTextAreaElement>('textarea')!.value;
      const mode = document.querySelector<HTMLSelectElement>('[name="textmode_0_root"]')!.value;
      expect(decodeCustomFieldText(source, mode) === value).toBe(true);
      expect(mode).toBe(customFieldTextMode(value));
    },
  );

  it('maps opaque enum tokens to immutable CR/LF choices in both parser paths', () => {
    const choices = ['one\rtwo', 'one\r\ntwo', 'one\ntwo'];
    const schema = envelopeSchema({ type: 'string', minLength: 1, maxLength: 32, enum: choices });
    const item = field({
      definition: {
        ...field().definition,
        schema,
        formLabels: [
          {
            location: '/',
            labels: null,
            choices: choices.map((_, index) => ({ en: 'Choice ' + index })),
          },
        ],
      },
      envelope: { value: choices[1]! },
    });
    const data = fields([item]);
    const patch = {
      set: [
        {
          conceptKey: item.conceptKey,
          definitionId: item.definitionId,
          schemaVersion: 1,
          envelope: { value: null },
          consentEvidenceId: null,
        },
      ],
      remove: [],
    };
    const { container } = render(
      <CustomFieldNativeForm
        action="/api/account/custom-fields"
        patch={patch}
        schemata={[schema]}
        complex={false}
        copy={enAdmin.custom_fields}
      >
        <input type="hidden" name="csrfToken" value={'A'.repeat(43)} />
        <input type="hidden" name="idempotencyKey" value={'K'.repeat(43)} />
        <input type="hidden" name="version" value={data.version} />
        <input type="hidden" name="payload" value={JSON.stringify(patch)} />
        <FieldControls
          schema={schema.properties.value}
          labels={item.definition.labels}
          formLabels={item.definition.formLabels}
          locale="en"
          defaultLocale="en"
          copy={enAdmin.custom_fields}
          prefix="0"
          value={choices[1]}
        />
      </CustomFieldNativeForm>,
    );
    const form = container.querySelector('form')!;
    expect(form.querySelector<HTMLSelectElement>('[name="answer_0_root"]')!.value).toBe(
      customFieldChoiceToken(1),
    );
    expect(
      JSON.parse(String(enhancedSubmission(form).get('payload'))).set[0].envelope.value ===
        choices[1],
    ).toBe(true);
    const html = renderStaticCustomFieldControls({
      schema: schema.properties.value,
      labels: item.definition.labels,
      formLabels: item.definition.formLabels,
      locale: 'en',
      defaultLocale: 'en',
      copy: enAdmin.custom_fields,
      prefix: '0',
      value: choices[1],
    });
    const document = new DOMParser().parseFromString(html, 'text/html');
    expect(document.querySelector<HTMLSelectElement>('select')!.value).toBe('choice-1');
    expect(
      [...document.querySelectorAll('option')].every((option) => !/[\r\n]/.test(option.value)),
    ).toBe(true);
    const nativeData = new URLSearchParams({
      csrfToken: 'proof',
      idempotencyKey: 'custom-key',
      version: data.version,
      payload: JSON.stringify(patch),
      answer_0_root: 'choice-1',
    });
    const result = customFieldForm('/api/account/custom-fields', nativeData, data);
    if (result.operation.kind !== 'setOwnCustomFields') throw new Error('Wrong form operation');
    expect(JSON.parse(String(result.operation.patch)).set[0].envelope.value === choices[1]).toBe(
      true,
    );
  });

  it('preserves text and editable names after rejection and switches exact modes without losing CR', () => {
    const schema = envelopeSchema({ type: 'string', minLength: 1, maxLength: 32 });
    const { container } = render(
      <CustomFieldNativeForm
        action="/api/account/custom-fields"
        patch={{
          set: [
            {
              conceptKey: 'text',
              definitionId,
              schemaVersion: 1,
              envelope: { value: null },
              consentEvidenceId: null,
            },
          ],
          remove: [],
        }}
        schemata={[schema]}
        complex={false}
        copy={enAdmin.custom_fields}
      >
        <input type="hidden" name="payload" value="" />
        <FieldControls
          schema={schema.properties.value}
          labels={{ en: 'Text' }}
          locale="en"
          defaultLocale="en"
          copy={enAdmin.custom_fields}
          prefix="0"
          value={'a\rb'}
        />
      </CustomFieldNativeForm>,
    );
    const form = container.querySelector('form')!;
    const mode = form.querySelector<HTMLSelectElement>('[name="textmode_0_root"]')!;
    const textarea = form.querySelector<HTMLTextAreaElement>('textarea')!;
    expect(mode.value).toBe('escaped');
    expect(mode.options[0]!.disabled).toBe(true);
    fireEvent.change(textarea, { target: { value: 'bad\\escape' } });
    fireEvent.submit(form);
    expect(form.querySelector('[name="answer_0_root"]')).not.toBeNull();
    expect(textarea.value).toBe('bad\\escape');
    fireEvent.change(textarea, { target: { value: 'a\\nb' } });
    fireEvent.change(mode, { target: { value: 'plain' } });
    expect(textarea.value).toBe('a\nb');
    fireEvent.change(mode, { target: { value: 'escaped' } });
    expect(textarea.value).toBe('a\\nb');
  });

  it.each([
    { type: 'boolean' },
    { type: 'integer', minimum: 5, maximum: 20 },
    { type: 'number', minimum: -3.5, maximum: 4.25 },
    { type: 'string', minLength: 2, maxLength: 17 },
  ] as const)('keeps an unchanged native scalar draft schema and authored metadata', (shape) => {
    const initial: CustomFieldDefinition = {
      definitionId,
      targetEntity: 'Membership',
      key: 'scalar',
      schemaVersion: 3,
      version: '7',
      status: 'Draft',
      activeRevision: null,
      isDiscarded: false,
      maximumValueBytes: 128,
      schema: envelopeSchema(shape),
      labels: { en: 'Scalar' },
      piiCategory: 'None',
      isRequired: true,
      sort: 2,
      purposeKey: null,
      notices: null,
      formLabels: [],
    };
    const html = renderToStaticMarkup(
      <CustomFieldDetails
        snapshot={snapshot()}
        view={{
          kind: 'custom_field',
          definition: initial,
          revisions: {
            items: [initial],
            pageInfo: {
              nextCursor: null,
              previousCursor: null,
              hasNext: false,
              hasPrevious: false,
            },
          },
          expectedIncumbentId: null,
          expectedIncumbentVersion: null,
        }}
        locale="en"
        copy={enAdmin.custom_fields}
      />,
    );
    const document = new DOMParser().parseFromString(html, 'text/html');
    const form = document.querySelector<HTMLFormElement>('form.custom-field-native-scalar')!;
    expect(form).not.toBeNull();
    expect(form.querySelector<HTMLSelectElement>('[name="authorType"]')!.value).toBe(shape.type);
    const result = customFieldForm(
      '/api/studio/custom-field-revise',
      new URLSearchParams([...new FormData(form)].map(([name, value]) => [name, String(value)])),
    );
    if (result.operation.kind !== 'reviseCustomFieldDefinition')
      throw new Error('Wrong form operation');
    const proposal = JSON.parse(String(result.operation.input));
    expect(proposal.schema).toEqual(initial.schema);
    expect(proposal.formLabels).toEqual(initial.formLabels);
    expect(proposal.labels).toEqual(initial.labels);
    expect(result.operation).toMatchObject({ version: initial.version });
  });

  it.each([
    { type: 'string', minLength: 1, maxLength: 16, enum: ['one', 'two'] },
    { type: 'array', minItems: 0, maxItems: 2, items: { type: 'boolean' } },
  ] as const)('withholds the lossy native scalar editor for enum or nested revisions', (shape) => {
    const initial: CustomFieldDefinition = {
      definitionId,
      targetEntity: 'Membership',
      key: 'complex',
      schemaVersion: 1,
      version: '7',
      status: 'Draft',
      activeRevision: null,
      isDiscarded: false,
      maximumValueBytes: 128,
      schema: envelopeSchema(shape),
      labels: { en: 'Complex' },
      piiCategory: 'None',
      isRequired: false,
      sort: 0,
      purposeKey: null,
      notices: null,
      formLabels:
        shape.type === 'array'
          ? [{ location: '/0', labels: { en: 'Item' }, choices: null }]
          : [{ location: '/', labels: null, choices: [{ en: 'One' }, { en: 'Two' }] }],
    };
    const html = renderToStaticMarkup(
      <CustomFieldDetails
        snapshot={snapshot()}
        view={{
          kind: 'custom_field',
          definition: initial,
          revisions: {
            items: [initial],
            pageInfo: {
              nextCursor: null,
              previousCursor: null,
              hasNext: false,
              hasPrevious: false,
            },
          },
          expectedIncumbentId: null,
          expectedIncumbentVersion: null,
        }}
        locale="en"
        copy={enAdmin.custom_fields}
      />,
    );
    const document = new DOMParser().parseFromString(html, 'text/html');
    expect(document.querySelector('form.custom-field-native-scalar')).toBeNull();
    expect(document.querySelector('textarea[name="payload"]')).toBeNull();
    expect(document.body.textContent).toContain(enAdmin.custom_fields.scalar_editor_unavailable);
  });

  it('requires an explicit data category and builds a schema object for the native POST', () => {
    render(
      <CustomFieldBuilder
        csrfToken={'A'.repeat(43)}
        formKey={'K'.repeat(43)}
        locales={['en', 'tr']}
        defaultLocale="en"
        locale="en"
        copy={enAdmin.custom_fields}
      />,
    );
    const category = screen.getByLabelText(enAdmin.custom_fields.category);
    expect(category).toHaveValue('');
    fireEvent.change(screen.getByLabelText(enAdmin.custom_fields.key), {
      target: { value: 'example' },
    });
    fireEvent.change(screen.getByLabelText('Field label (en)'), { target: { value: 'Example' } });
    fireEvent.change(category, { target: { value: 'None' } });
    const form = screen
      .getByRole('button', { name: enAdmin.custom_fields.create })
      .closest('form')!;
    const payload = form.querySelector<HTMLInputElement>('input[name="payload"]')!;
    const parsed = JSON.parse(payload.value);
    expect(parsed.piiCategory).toBe('None');
    expect(parsed.schema.properties.value).toEqual({ type: 'string', minLength: 1, maxLength: 32 });
    expect(parsed.formLabels).toBeNull();
    expect(form.action).toContain('/api/studio/custom-field-create');
    fireEvent.change(screen.getByLabelText(enAdmin.custom_fields.order), {
      target: { value: '-1' },
    });
    expect(payload.value).toBe('');
  });
  function authorTextDefinition(value: string, enumeration = false): CustomFieldDefinition {
    return {
      definitionId,
      targetEntity: 'Membership',
      key: 'author-exact',
      schemaVersion: 1,
      version: '7',
      status: 'Draft',
      activeRevision: null,
      isDiscarded: false,
      maximumValueBytes: 128,
      schema: envelopeSchema(
        enumeration
          ? { type: 'string', minLength: 1, maxLength: 24, enum: [value] }
          : { type: 'boolean' },
      ),
      labels: { en: value },
      piiCategory: 'Sensitive',
      isRequired: false,
      sort: 0,
      purposeKey: 'purpose',
      notices: { en: value },
      formLabels: enumeration ? [{ location: '/', labels: null, choices: [{ en: value }] }] : null,
    };
  }
  it.each([
    'plain',
    'line\nfeed',
    'carriage\rreturn',
    'windows\r\nlines',
    'mixed\r\nthen\nthen\r',
    '\nstart\n',
    'back\\slash',
  ])(
    'preserves enum literals and every visible author text family in the visual builder',
    (value) => {
      const initial = authorTextDefinition(value, true);
      const { container } = render(
        <CustomFieldBuilder
          initial={initial}
          csrfToken={'A'.repeat(43)}
          formKey={'K'.repeat(43)}
          locales={['en']}
          defaultLocale="en"
          locale="en"
          copy={enAdmin.custom_fields}
        />,
      );
      const proposal = JSON.parse(
        container.querySelector<HTMLInputElement>('[name="payload"]')!.value,
      );
      expect(proposal.schema).toEqual(initial.schema);
      expect(proposal.labels).toEqual(initial.labels);
      expect(proposal.notices).toEqual(initial.notices);
      expect(proposal.formLabels).toEqual(initial.formLabels);
      const literal = container.querySelector<HTMLTextAreaElement>('#definition-control-choice-0')!;
      expect(literal.tagName).toBe('TEXTAREA');
      expect(literal.value).toBe(encodeCustomFieldText(value, customFieldTextMode(value)));
      const mode = container.querySelector<HTMLSelectElement>(
        '#definition-control-choice-0-textmode',
      )!;
      fireEvent.change(mode, { target: { value: 'escaped' } });
      fireEvent.change(literal, { target: { value: 'first\\r\\nline' } });
      expect(
        JSON.parse(container.querySelector<HTMLInputElement>('[name="payload"]')!.value).schema
          .properties.value.enum,
      ).toEqual(['first\r\nline']);
    },
  );
  it('blanks stale valid author proposals for malformed escapes and decoded literal or metadata overflow', () => {
    const { container } = render(
      <CustomFieldBuilder
        initial={authorTextDefinition('Original', true)}
        csrfToken={'A'.repeat(43)}
        formKey={'K'.repeat(43)}
        locales={['en']}
        defaultLocale="en"
        locale="en"
        copy={enAdmin.custom_fields}
      />,
    );
    const form = container.querySelector<HTMLFormElement>('form')!;
    const payload = form.querySelector<HTMLInputElement>('[name="payload"]')!;
    const literal = form.querySelector<HTMLTextAreaElement>('#definition-control-choice-0')!;
    fireEvent.change(form.querySelector('#definition-control-choice-0-textmode')!, {
      target: { value: 'escaped' },
    });
    fireEvent.change(literal, { target: { value: '\\q' } });
    expect(payload.value).toBe('');
    expect(literal.checkValidity()).toBe(false);
    expect(fireEvent.submit(form)).toBe(false);
    fireEvent.change(literal, { target: { value: '\\r'.repeat(25) } });
    expect(payload.value).toBe('');
    fireEvent.change(literal, { target: { value: '\\r' } });
    expect(JSON.parse(payload.value).schema.properties.value.enum).toEqual(['\r']);
    const label = form.querySelector<HTMLTextAreaElement>('#definition-control-label-en')!;
    fireEvent.change(label, { target: { value: 'é'.repeat(65) } });
    expect(payload.value).toBe('');
    expect(label.checkValidity()).toBe(false);
    fireEvent.change(label, { target: { value: 'Corrected' } });
    expect(JSON.parse(payload.value).labels).toEqual({ en: 'Corrected' });
  });
  it.each([
    'plain',
    'line\nfeed',
    'carriage\rreturn',
    'windows\r\nlines',
    'mixed\r\nthen\nthen\r',
    '\nstart\n',
    'back\\slash',
  ])(
    'round-trips sensitive root labels and notice bytes through no-JavaScript authoring and native recovery',
    (value) => {
      const initial = authorTextDefinition(value);
      const prepared = snapshot();
      const html = renderToStaticMarkup(
        <CustomFieldDetails
          snapshot={prepared}
          view={{
            kind: 'custom_field',
            definition: initial,
            revisions: {
              items: [initial],
              pageInfo: {
                nextCursor: null,
                previousCursor: null,
                hasNext: false,
                hasPrevious: false,
              },
            },
            expectedIncumbentId: null,
            expectedIncumbentVersion: null,
          }}
          locale="en"
          copy={enAdmin.custom_fields}
        />,
      );
      const document = new DOMParser().parseFromString(html, 'text/html');
      const form = document.querySelector<HTMLFormElement>('form.custom-field-native-scalar')!;
      const fields = new URLSearchParams(
        [...new FormData(form)].map(([name, content]) => [
          name,
          String(content).replace(/\r?\n/g, '\r\n'),
        ]),
      );
      const result = customFieldForm('/api/studio/custom-field-revise', fields);
      if (result.operation.kind !== 'reviseCustomFieldDefinition')
        throw new Error('Wrong operation');
      expect(JSON.parse(String(result.operation.input))).toMatchObject({
        labels: initial.labels,
        notices: initial.notices,
        schema: initial.schema,
      });
      const recovery = customFieldMutationContent(
        {
          kind: 'mutation_error',
          action: '/api/studio/custom-field-revise',
          destination: '/studio/custom-fields/' + definitionId,
          fields: [...fields],
          reason: 'validation',
          retryable: false,
        },
        'en',
        prepared.site,
      )!;
      const recovered = new DOMParser()
        .parseFromString(recovery, 'text/html')
        .querySelector<HTMLFormElement>('form')!;
      const resubmitted = new URLSearchParams(
        [...new FormData(recovered)].map(([name, content]) => [
          name,
          String(content).replace(/\r?\n/g, '\r\n'),
        ]),
      );
      const retry = customFieldForm('/api/studio/custom-field-revise', resubmitted);
      if (retry.operation.kind !== 'reviseCustomFieldDefinition')
        throw new Error('Wrong operation');
      expect(JSON.parse(String(retry.operation.input))).toMatchObject({
        labels: initial.labels,
        notices: initial.notices,
        schema: initial.schema,
      });
      expect(retry.operation.version).toBe(initial.version);
    },
  );

  it('preserves nested control labels while refusing malformed notice and choice-label escapes', () => {
    const initial = authorTextDefinition('Root\r\nlabel', true);
    const nested: CustomFieldDefinition = {
      ...initial,
      schema: envelopeSchema({
        type: 'object',
        properties: {
          child: { type: 'string', minLength: 1, maxLength: 24, enum: ['Choice\r\nvalue'] },
        },
        required: ['child'],
        additionalProperties: false,
      }),
      formLabels: [
        { location: '/0', labels: { en: 'Child\rlabel\n' }, choices: [{ en: 'Choice\r\nlabel' }] },
      ],
    };
    const { container } = render(
      <CustomFieldBuilder
        initial={nested}
        csrfToken={'A'.repeat(43)}
        formKey={'K'.repeat(43)}
        locales={['en']}
        defaultLocale="en"
        locale="en"
        copy={enAdmin.custom_fields}
      />,
    );
    const payload = container.querySelector<HTMLInputElement>('[name="payload"]')!;
    expect(JSON.parse(payload.value).formLabels).toEqual(nested.formLabels);
    const choice = container.querySelector<HTMLTextAreaElement>(
      '#definition-control-0-choice-0-en',
    )!;
    fireEvent.change(choice, { target: { value: '\\q' } });
    expect(payload.value).toBe('');
    fireEvent.change(choice, { target: { value: 'Correct\\r\\nlabel' } });
    expect(JSON.parse(payload.value).formLabels[0].choices).toEqual([{ en: 'Correct\r\nlabel' }]);
    const notice = container.querySelector<HTMLTextAreaElement>('#definition-notice-en')!;
    fireEvent.change(notice, { target: { value: 'malformed\\' } });
    expect(payload.value).toBe('');
    fireEvent.change(notice, { target: { value: '' } });
    expect(payload.value).toBe('');
    fireEvent.change(container.querySelector('#definition-category')!, {
      target: { value: 'None' },
    });
    expect(JSON.parse(payload.value).notices).toBeNull();
  });
});
