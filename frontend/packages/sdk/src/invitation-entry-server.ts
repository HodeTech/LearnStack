import {
  fields,
  identifier,
  invitationEmail,
  object,
  text,
  version,
} from './administration-policy.js';
import type { ApiResult } from './api-result.js';
import type { components } from './generated/schema.js';
import { appError, parseProblem } from './response-policy.js';

export type InvitationEntryInput = components['schemas']['InvitationEntryInput'];
export type InvitationInspection = components['schemas']['InvitationInspectionResponse'];
export type InvitationAcceptance = components['schemas']['InvitationAcceptanceResponse'];
export type InvitationEntryFetch = (path: string, init: RequestInit) => Promise<Response>;
export type InvitationEntryOptions = Readonly<{ signal?: AbortSignal }>;
export type InvitationAcceptOptions = InvitationEntryOptions & Readonly<{ version: string }>;
const base = '/api/v1/identity/invitation-entry/';
const roles = new Set([
  'TenantAdmin:Tenant',
  'Instructor:Tenant',
  'Learner:Tenant',
  'Guardian:Tenant',
  'Editor:Tenant',
  'OrgAdmin:Organization',
  'OrgInstructor:Organization',
  'Learner:Organization',
]);
function input(value: unknown): value is InvitationEntryInput {
  return (
    fields(value, ['invitationId', 'code']) &&
    identifier(value.invitationId) &&
    typeof value.code === 'string' &&
    value.code.startsWith(value.invitationId + '.') &&
    /^[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]$/.test(value.code.slice(37))
  );
}
function success(value: unknown, inspection: boolean, id: string): boolean {
  if (
    !fields(
      value,
      inspection
        ? [
            'invitationId',
            'version',
            'expiresAt',
            'tenantLabel',
            'organizationLabel',
            'roleId',
            'roleKey',
            'roleScope',
            'verifiedEmail',
          ]
        : ['invitationId', 'version', 'membershipId', 'disposition', 'tenantId', 'organizationId'],
    ) ||
    value.invitationId !== id ||
    !version(value.version)
  )
    return false;
  if (!inspection)
    return (
      identifier(value.membershipId) &&
      identifier(value.tenantId) &&
      (value.organizationId === null || identifier(value.organizationId)) &&
      (value.disposition === 'created' || value.disposition === 'already_member')
    );
  return (
    // Tenancy accepts control characters in a stored name; the renderer shows one clean line.
    text(value.tenantLabel, 200, false, true) &&
    (value.organizationLabel === null || text(value.organizationLabel, 200, false, true)) &&
    identifier(value.roleId) &&
    roles.has(String(value.roleKey) + ':' + String(value.roleScope)) &&
    (value.roleScope === 'Tenant'
      ? value.organizationLabel === null
      : value.organizationLabel !== null) &&
    invitationEmail(value.verifiedEmail) &&
    value.verifiedEmail === value.verifiedEmail.toLowerCase() &&
    typeof value.expiresAt === 'string' &&
    /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(value.expiresAt) &&
    Number.isFinite(Date.parse(value.expiresAt)) &&
    new Date(value.expiresAt).toISOString().slice(0, 19) === value.expiresAt.slice(0, 19)
  );
}
class BodyLimit extends Error {
  constructor() {
    super('Invitation response refused');
  }
}
async function read(response: Response, signal?: AbortSignal): Promise<unknown> {
  const reader = response.body?.getReader();
  if (!reader) throw new SyntaxError('Invitation response refused');
  let cancelling: Promise<void> | undefined;
  const cancel = () => {
    cancelling ??= reader.cancel().catch(() => undefined);
  };
  signal?.addEventListener('abort', cancel, { once: true });
  const chunks: Uint8Array[] = [];
  let bytes: Uint8Array | undefined;
  let size = 0;
  try {
    if (signal?.aborted) cancel();
    const declared = response.headers.get('Content-Length');
    if (declared !== null && /^[0-9]+$/.test(declared) && Number(declared) > 16 * 1024) {
      cancel();
      throw new BodyLimit();
    }
    for (;;) {
      const chunk = await reader.read();
      if (signal?.aborted) {
        chunk.value?.fill(0);
        throw new DOMException('Cancelled', 'AbortError');
      }
      if (chunk.done) break;
      size += chunk.value.byteLength;
      if (size > 16 * 1024) {
        chunk.value.fill(0);
        cancel();
        throw new BodyLimit();
      }
      chunks.push(chunk.value);
    }
    bytes = new Uint8Array(size);
    let offset = 0;
    for (const chunk of chunks) {
      bytes.set(chunk, offset);
      offset += chunk.byteLength;
    }
    try {
      return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)) as unknown;
    } catch {
      throw new SyntaxError('Invitation response refused');
    }
  } finally {
    bytes?.fill(0);
    for (const chunk of chunks) chunk.fill(0);
    signal?.removeEventListener('abort', cancel);
    if (cancelling) await cancelling;
    reader.releaseLock();
  }
}
/** Three exact POSTs. The injected native server transport owns current credentials and source proof. */
export function createInvitationEntryServerSdk(transport: InvitationEntryFetch) {
  async function request<T>(
    action: 'inspections' | 'acceptances' | 'results',
    value: InvitationEntryInput,
    options?: InvitationEntryOptions | InvitationAcceptOptions,
  ): Promise<ApiResult<T>> {
    let signal: AbortSignal | undefined;
    let body: string;
    let invitationId: string;
    const headers: Record<string, string> = {
      Accept: 'application/json',
      'Content-Type': 'application/json',
    };
    try {
      if (
        !input(value) ||
        (options !== undefined &&
          (!object(options) ||
            Reflect.ownKeys(options).some(
              (key) => key !== 'signal' && !(action === 'acceptances' && key === 'version'),
            )))
      )
        return { kind: 'invalid-request' };
      signal = options?.signal;
      if (signal !== undefined && !(signal instanceof AbortSignal))
        return { kind: 'invalid-request' };
      if (signal?.aborted) return { kind: 'cancelled' };
      if (action === 'acceptances') {
        if (
          !options ||
          !Object.hasOwn(options, 'version') ||
          !('version' in options) ||
          !version(options.version)
        )
          return { kind: 'invalid-request' };
        headers['If-Match'] = '"' + options.version + '"';
      }
      invitationId = value.invitationId;
      body = JSON.stringify({ invitationId, code: value.code });
    } catch {
      return { kind: 'invalid-request' };
    }
    let response: Response;
    try {
      response = await transport(base + action, {
        method: 'POST',
        cache: 'no-store',
        headers,
        body,
        signal,
      });
    } catch {
      return { kind: signal?.aborted ? 'cancelled' : 'transport-error' };
    }
    if (response.status !== 200 && response.status < 400) {
      try {
        await response.body?.cancel();
      } catch {
        /* Fixed bounded refusal only. */
      }
      return signal?.aborted
        ? { kind: 'cancelled' }
        : { kind: 'invalid-response', reason: 'unexpected-status' };
    }
    let parsed: unknown;
    try {
      parsed = await read(response, signal);
    } catch (error) {
      if (signal?.aborted) return { kind: 'cancelled' };
      if (error instanceof SyntaxError)
        return { kind: 'invalid-response', reason: 'malformed-json' };
      if (error instanceof BodyLimit)
        return {
          kind: 'invalid-response',
          reason: response.status === 200 ? 'invalid-success' : 'invalid-problem',
        };
      return { kind: 'transport-error' };
    }
    if (signal?.aborted) return { kind: 'cancelled' };
    if (response.status !== 200) {
      const problem = parseProblem(parsed, response.status);
      return problem
        ? {
            kind: 'api-error',
            status: response.status,
            problem,
            error: appError(problem, response.headers.get('Retry-After')),
          }
        : { kind: 'invalid-response', reason: 'invalid-problem' };
    }
    if (
      response.headers.get('Content-Type')?.split(';', 1)[0]?.trim().toLowerCase() !==
        'application/json' ||
      !success(parsed, action === 'inspections', invitationId) ||
      response.headers.get('ETag') !== '"' + (parsed as { version: string }).version + '"' ||
      response.headers.get('Cache-Control') !== 'no-store' ||
      response.headers.has('Location') ||
      response.headers.has('Idempotency-Replayed') ||
      response.headers.has('Set-Cookie')
    )
      return { kind: 'invalid-response', reason: 'invalid-success' };
    return { kind: 'success', status: 200, data: parsed as T };
  }
  return Object.freeze({
    inspectInvitation: (value: InvitationEntryInput, options?: InvitationEntryOptions) =>
      request<InvitationInspection>('inspections', value, options),
    acceptInvitation: (value: InvitationEntryInput, options: InvitationAcceptOptions) =>
      request<InvitationAcceptance>('acceptances', value, options),
    /** Explicit recovery after an uncertain acceptance; no automatic retry or conditional header. */
    getOwnInvitationResult: (value: InvitationEntryInput, options?: InvitationEntryOptions) =>
      request<InvitationAcceptance>('results', value, options),
  });
}
export type InvitationEntryServerSdk = ReturnType<typeof createInvitationEntryServerSdk>;
