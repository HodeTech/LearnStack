import type {
  AdministrationFetch,
  AdministrationResult,
  AdministrationCreateOptions,
  AdministrationWriteOptions,
  AdministrationReadOptions,
  AdministrationPageOptions,
} from './administration-contract.js';
import { fields, identifier, object, text, version } from './administration-policy.js';
import type {
  ConsentGrant,
  ConsentHistory,
  ConsentReceipt,
  ConsentScopes,
  ConsentWithdrawal,
  CustomFieldInput,
  CustomFieldPage,
  CustomFieldDefinition,
  CustomFieldPatch,
  CustomFieldReceipt,
  CustomFieldPageOptions,
  MembershipCustomFields,
  PersonalConsent,
} from './custom-field-contract.js';
import { parseCustomFieldJson } from './custom-field-json.js';
import {
  consent,
  consentGrant,
  consentReceipt,
  consentWithdrawal,
  customPage,
  customPatch,
  definition,
  fieldInput,
  membershipFields,
  positive,
  scope,
} from './custom-field-policy.js';
import { appError, parseProblem } from './response-policy.js';
export type * from './custom-field-contract.js';
export { parseCustomFieldJson, parseCustomFieldNumber } from './custom-field-json.js';
export { customFieldSchema as isCustomFieldSchema } from './custom-field-schema-policy.js';

/** Closed P03-5 wrappers. A raw form payload keeps its numeric lexemes until API admission. */
export function createCustomFieldServerSdk(transport: AdministrationFetch) {
  async function request<T>(
    path: string | undefined,
    valid: (value: unknown) => boolean,
    options?: AdministrationReadOptions,
    mutation?: {
      method: string;
      input: unknown;
      write: AdministrationCreateOptions;
      version?: string;
      max?: number;
      creation?: boolean;
    },
  ): Promise<AdministrationResult<T, 200 | 201>> {
    const signal = options?.signal;
    if (signal?.aborted) return { kind: 'cancelled' };
    let body: string | undefined;
    const headers: Record<string, string> = { Accept: 'application/json' };
    try {
      if (!path) return { kind: 'invalid-request' };
      if (mutation) {
        if (
          !/^[\x21-\x7e]{8,128}$/.test(mutation.write.idempotencyKey) ||
          (mutation.version !== undefined && !version(mutation.version))
        )
          return { kind: 'invalid-request' };
        body = typeof mutation.input === 'string' ? mutation.input : JSON.stringify(mutation.input);
        parseCustomFieldJson(body, mutation.max ?? 4096);
        headers['Content-Type'] = 'application/json';
        headers['Idempotency-Key'] = mutation.write.idempotencyKey;
        if (mutation.version !== undefined) headers['If-Match'] = '"' + mutation.version + '"';
      }
    } catch {
      return { kind: 'invalid-request' };
    }
    let response: Response;
    try {
      response = await transport(path, {
        method: mutation?.method ?? 'GET',
        cache: 'no-store',
        headers,
        body,
        signal,
      });
    } catch {
      return { kind: signal?.aborted ? 'cancelled' : 'transport-error' };
    }
    const expected = mutation?.creation ? 201 : 200;
    if (response.status !== expected && response.status < 400) {
      await response.body?.cancel().catch(() => undefined);
      return { kind: 'invalid-response', reason: 'unexpected-status' };
    }
    let value: unknown;
    const reader = response.body?.getReader();
    if (!reader) return { kind: 'invalid-response', reason: 'malformed-json' };
    let cancellation: Promise<void> | undefined;
    const cancel = () => {
      cancellation ??= reader.cancel().catch(() => undefined);
    };
    signal?.addEventListener('abort', cancel, { once: true });
    try {
      let bytes = 0;
      const chunks: Uint8Array[] = [];
      for (;;) {
        if (signal?.aborted) {
          cancel();
          return { kind: 'cancelled' };
        }
        const next = await reader.read();
        if (next.done) break;
        bytes += next.value.byteLength;
        if (bytes > 256 * 1024) {
          cancel();
          return { kind: 'invalid-response', reason: 'invalid-success' };
        }
        chunks.push(next.value);
      }
      const result = new Uint8Array(bytes);
      let offset = 0;
      for (const chunk of chunks) {
        result.set(chunk, offset);
        offset += chunk.length;
      }
      value = parseCustomFieldJson(
        new TextDecoder('utf-8', { fatal: true }).decode(result),
        256 * 1024,
      );
    } catch {
      return signal?.aborted
        ? { kind: 'cancelled' }
        : { kind: 'invalid-response', reason: 'malformed-json' };
    } finally {
      signal?.removeEventListener('abort', cancel);
      if (cancellation) await cancellation;
      reader.releaseLock();
    }
    if (signal?.aborted) return { kind: 'cancelled' };
    if (response.status !== expected) {
      const problem = parseProblem(value, response.status);
      if (!problem) return { kind: 'invalid-response', reason: 'invalid-problem' };
      const error = appError(problem, response.headers.get('Retry-After'));
      return {
        kind: 'api-error',
        status: response.status,
        problem,
        error: error.code === 'concurrency_conflict' ? { code: 'concurrency_conflict' } : error,
      };
    }
    const etag = response.headers.get('ETag');
    const location = response.headers.get('Location');
    const replayed = response.headers.get('Idempotency-Replayed');
    const versioned = object(value) && Object.hasOwn(value, 'version');
    if (
      response.headers.get('Content-Type')?.split(';', 1)[0]?.trim().toLowerCase() !==
        'application/json' ||
      !valid(value) ||
      (versioned ? !object(value) || etag !== '"' + value.version + '"' : etag !== null) ||
      (mutation?.creation
        ? !object(value) ||
          location !== '/api/v1/customization/custom-field-definitions/' + value.id
        : location !== null) ||
      (replayed !== null && (!mutation || replayed !== 'true'))
    )
      return { kind: 'invalid-response', reason: 'invalid-success' };
    return {
      kind: 'success',
      status: expected,
      data: value as T,
      etag,
      location,
      replayed: replayed === 'true',
    };
  }
  const definitions = '/api/v1/customization/custom-field-definitions';
  const consents = '/api/v1/tenancy/membership-field-consents';
  const resource = (prefix: string, id: string) => (identifier(id) ? prefix + '/' + id : undefined);
  const page = (
    path: string,
    options?: AdministrationPageOptions,
    extras: Record<string, string> = {},
  ) => {
    if (
      (options?.cursor !== undefined && !text(options.cursor, 1024)) ||
      (options?.limit !== undefined && (!positive(options.limit) || options.limit > 20))
    )
      return undefined;
    const query = new URLSearchParams(extras);
    if (options?.cursor) query.set('cursor', options.cursor);
    if (options?.limit) query.set('limit', String(options.limit));
    return path + (query.size ? '?' + query : '');
  };
  const receipt = (value: unknown) =>
    fields(value, ['id', 'version']) && identifier(value.id) && version(value.version);
  const input = (value: unknown, valid: (value: unknown) => boolean) => {
    try {
      return valid(
        typeof value === 'string'
          ? parseCustomFieldJson(value)
          : parseCustomFieldJson(JSON.stringify(value)),
      );
    } catch {
      return false;
    }
  };
  const mutate = <T>(
    path: string | undefined,
    method: string,
    body: unknown,
    valid: (value: unknown) => boolean,
    write: AdministrationCreateOptions,
    condition?: string,
    creation = false,
  ) =>
    input(body, valid)
      ? request<T>(path, receipt, write, {
          method,
          input: body,
          write,
          version: condition,
          creation,
        })
      : Promise.resolve({ kind: 'invalid-request' } as const);
  const patch = (
    path: string | undefined,
    body: CustomFieldPatch | string,
    write: AdministrationWriteOptions,
  ) => mutate<CustomFieldReceipt>(path, 'PATCH', body, customPatch, write, write.version);
  const act = (
    path: string | undefined,
    body: unknown,
    valid: (value: unknown) => boolean,
    write: AdministrationCreateOptions,
  ) =>
    input(body, valid)
      ? request<ConsentReceipt>(path, consentReceipt, write, { method: 'POST', input: body, write })
      : Promise.resolve({ kind: 'invalid-request' } as const);
  return Object.freeze({
    listCustomFieldDefinitions: (options?: CustomFieldPageOptions) =>
      request<CustomFieldPage>(
        options?.status !== undefined && !['Draft', 'Active', 'Deprecated'].includes(options.status)
          ? undefined
          : page(
              definitions,
              options ? { ...options, limit: undefined } : undefined,
              options?.status ? { status: options.status } : {},
            ),
        (value) => customPage(value, definition),
        options,
      ),
    getCustomFieldDefinition: (id: string, options?: AdministrationReadOptions) =>
      request<CustomFieldDefinition>(resource(definitions, id), definition, options),
    listCustomFieldRevisions: (id: string, options?: AdministrationPageOptions) =>
      request<CustomFieldPage>(
        identifier(id)
          ? page(
              definitions + '/' + id + '/revisions',
              options ? { ...options, limit: undefined } : undefined,
            )
          : undefined,
        (value) => customPage(value, definition),
        options,
      ),
    registerCustomFieldDefinition: (
      body: CustomFieldInput | string,
      write: AdministrationCreateOptions,
    ) => mutate<CustomFieldReceipt>(definitions, 'POST', body, fieldInput, write, undefined, true),
    reviseCustomFieldDefinition: (
      id: string,
      body: CustomFieldInput | string,
      write: AdministrationWriteOptions,
    ) =>
      mutate<CustomFieldReceipt>(
        resource(definitions, id),
        'PUT',
        body,
        fieldInput,
        write,
        write.version,
      ),
    publishCustomFieldDefinition: (
      id: string,
      expectedIncumbentId: string | null,
      expectedIncumbentVersion: string | null,
      write: AdministrationWriteOptions,
    ) =>
      mutate<CustomFieldReceipt>(
        resource(definitions, id)?.concat('/publication'),
        'POST',
        { expectedIncumbentId, expectedIncumbentVersion },
        (value) =>
          fields(value, ['expectedIncumbentId', 'expectedIncumbentVersion']) &&
          (value.expectedIncumbentId === null
            ? value.expectedIncumbentVersion === null
            : identifier(value.expectedIncumbentId) && version(value.expectedIncumbentVersion)),
        write,
        write.version,
      ),
    retireCustomFieldDefinition: (id: string, write: AdministrationWriteOptions) =>
      mutate<CustomFieldReceipt>(
        resource(definitions, id)?.concat('/retirement'),
        'POST',
        {},
        (value) => fields(value, []),
        write,
        write.version,
      ),
    discardCustomFieldDefinition: (id: string, write: AdministrationWriteOptions) =>
      mutate<CustomFieldReceipt>(
        resource(definitions, id)?.concat('/discard'),
        'POST',
        {},
        (value) => fields(value, []),
        write,
        write.version,
      ),
    getMembershipCustomFields: (id: string, options?: AdministrationReadOptions) =>
      request<MembershipCustomFields>(
        resource('/api/v1/identity/memberships', id)?.concat('/custom-fields'),
        membershipFields,
        options,
      ),
    getOwnCustomFields: (options?: AdministrationReadOptions) =>
      request<MembershipCustomFields>(
        '/api/v1/identity/me/custom-fields',
        membershipFields,
        options,
      ),
    setMembershipCustomFields: (
      id: string,
      body: CustomFieldPatch | string,
      write: AdministrationWriteOptions,
    ) => patch(resource('/api/v1/identity/memberships', id)?.concat('/custom-fields'), body, write),
    setOwnCustomFields: (body: CustomFieldPatch | string, write: AdministrationWriteOptions) =>
      patch('/api/v1/identity/me/custom-fields', body, write),
    getOwnFieldConsent: (
      membershipId: string,
      definitionId: string,
      definitionVersion: number,
      options?: AdministrationReadOptions,
    ) =>
      request<Readonly<{ consent: PersonalConsent | null }>>(
        identifier(membershipId) && identifier(definitionId) && positive(definitionVersion)
          ? consents +
              '/own?' +
              new URLSearchParams({
                membershipId,
                definitionId,
                definitionVersion: String(definitionVersion),
              })
          : undefined,
        (value) => fields(value, ['consent']) && (value.consent === null || consent(value.consent)),
        options,
      ),
    listOwnConsentScopes: (options?: AdministrationPageOptions) =>
      request<ConsentScopes>(
        page(consents + '/own/scopes', options),
        (value) => customPage(value, scope),
        options,
      ),
    listOwnConsentHistory: (membershipId: string, options?: AdministrationPageOptions) =>
      request<ConsentHistory>(
        identifier(membershipId)
          ? page(consents + '/own/history', options, { membershipId })
          : undefined,
        (value) => customPage(value, consent),
        options,
      ),
    grantOwnFieldConsent: (body: ConsentGrant, write: AdministrationCreateOptions) =>
      act(consents + '/grants', body, consentGrant, write),
    withdrawOwnFieldConsent: (
      membershipId: string,
      body: ConsentWithdrawal,
      write: AdministrationCreateOptions,
    ) =>
      act(
        identifier(membershipId)
          ? consents + '/withdrawals?' + new URLSearchParams({ membershipId })
          : undefined,
        body,
        consentWithdrawal,
        write,
      ),
  });
}
export type CustomFieldServerSdk = ReturnType<typeof createCustomFieldServerSdk>;
