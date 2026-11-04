import { createInvitationEntryServerSdk } from '@learnstack/sdk/invitation-entry-server';

import { createNativeApiTransport, exactFields } from './native-api-transport.js';
import type { NativeApiConfiguration } from './native-api-transport.js';
import { NativeApiError } from './native-contract.js';
import type { NativeBinding } from './native-contract.js';
import { NativeInvitationError } from './native-invitation-contract.js';
import type {
  InvitationFailureCode,
  InvitationInput,
  InvitationInspection,
  InvitationResult,
  NativeInvitationApi,
} from './native-invitation-contract.js';

const failures: Readonly<Record<string, number>> = Object.freeze({
  validation_failed: 400,
  unauthorized: 401,
  forbidden: 403,
  not_found: 404,
  concurrency_conflict: 409,
  rate_limited: 429,
  dependency_unavailable: 503,
  audit_unavailable: 503,
  payload_too_large: 413,
});

/** The SDK owns wire validation; this adapter alone supplies the held credential and source. */
export function createNativeInvitationApi(
  configuration: NativeApiConfiguration,
  transport: typeof fetch,
): NativeInvitationApi {
  const request = createNativeApiTransport(configuration, transport);
  type Action = keyof NativeInvitationApi;
  async function call(
    action: Action,
    token: string,
    binding: NativeBinding,
    input: InvitationInput & Readonly<{ version?: string }>,
    signal: AbortSignal,
  ): Promise<InvitationInspection | InvitationResult> {
    if (
      !exactFields(
        input,
        action === 'accept' ? ['invitationId', 'code', 'version'] : ['invitationId', 'code'],
      )
    )
      throw new NativeApiError('configuration');
    let failure: unknown;
    const sdk = createInvitationEntryServerSdk(async (path, init) => {
      try {
        return await request(token, binding, null, path, init, signal);
      } catch (error) {
        failure = error;
        throw error;
      }
    });
    // Construct exact input fields; version is only a strong header on acceptance.
    const offer = { invitationId: input.invitationId, code: input.code };
    const result =
      action === 'inspect'
        ? await sdk.inspectInvitation(offer, { signal })
        : action === 'result'
          ? await sdk.getOwnInvitationResult(offer, { signal })
          : await sdk.acceptInvitation(offer, {
              signal,
              version: input.version ?? '',
            });
    if (signal.aborted) throw new NativeApiError('cancelled');
    if (result.kind === 'success') return Object.freeze({ ...result.data });
    if (result.kind === 'api-error') {
      const code =
        Object.hasOwn(failures, result.error.code) && failures[result.error.code] === result.status
          ? (result.error.code as InvitationFailureCode)
          : 'unknown';
      const delay = result.error.code === 'rate_limited' ? result.error.retryAfter : undefined;
      const retry =
        result.status === 429 &&
        delay !== undefined &&
        Number.isInteger(delay) &&
        delay >= 1 &&
        delay <= 86_400
          ? delay
          : undefined;
      throw new NativeInvitationError(code, result.status, retry);
    }
    if (failure instanceof NativeApiError) throw failure;
    throw new NativeApiError(
      result.kind === 'cancelled'
        ? 'cancelled'
        : result.kind === 'invalid-request'
          ? 'configuration'
          : result.kind === 'transport-error'
            ? 'transport'
            : 'response',
    );
  }
  return Object.freeze({
    inspect: (...args: Parameters<NativeInvitationApi['inspect']>) =>
      call('inspect', ...args) as Promise<InvitationInspection>,
    accept: (...args: Parameters<NativeInvitationApi['accept']>) =>
      call('accept', ...args) as Promise<InvitationResult>,
    result: (...args: Parameters<NativeInvitationApi['result']>) =>
      call('result', ...args) as Promise<InvitationResult>,
  });
}
