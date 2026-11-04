import type {
  InvitationAcceptance,
  InvitationEntryInput,
  InvitationInspection as Inspection,
} from '@learnstack/sdk/invitation-entry-server';

import { NativeApiError } from './native-contract.js';
import type { NativeBinding } from './native-contract.js';

export type InvitationInspection = Readonly<Inspection>;
export type InvitationResult = Readonly<InvitationAcceptance>;
export type NativeInvitationInspection = InvitationInspection;
export type NativeInvitationResult = InvitationResult;
export type InvitationInput = Readonly<InvitationEntryInput>;
export type NativeInvitationApi = Readonly<{
  inspect(
    token: string,
    binding: NativeBinding,
    input: InvitationInput,
    signal: AbortSignal,
  ): Promise<InvitationInspection>;
  accept(
    token: string,
    binding: NativeBinding,
    input: InvitationInput & Readonly<{ version: string }>,
    signal: AbortSignal,
  ): Promise<InvitationResult>;
  result(
    token: string,
    binding: NativeBinding,
    input: InvitationInput,
    signal: AbortSignal,
  ): Promise<InvitationResult>;
}>;

export type InvitationFailureCode =
  | 'validation_failed'
  | 'unauthorized'
  | 'forbidden'
  | 'not_found'
  | 'concurrency_conflict'
  | 'rate_limited'
  | 'dependency_unavailable'
  | 'audit_unavailable'
  | 'payload_too_large'
  | 'unknown';

/** No upstream messages, addresses, bodies or field errors leave the native adapter. */
export class NativeInvitationError extends NativeApiError {
  constructor(
    readonly code: InvitationFailureCode,
    status: number,
    retryAfterSeconds?: number,
  ) {
    super('provider_refused', status, retryAfterSeconds);
    this.name = 'NativeInvitationError';
  }
}

/** A safe native view, never a custody object or a generic submitted-fields draft. */
export type InvitationConfirmation = Readonly<{
  state:
    | 'signin'
    | 'bootstrap'
    | 'inspect'
    | 'forbidden'
    | 'ready'
    | 'accepted'
    | 'unknown'
    | 'unavailable'
    | 'expired'
    | 'account_change';
  csrfToken: string;
  inspection?: InvitationInspection;
  disposition?: 'created' | 'already_member';
  sessionAvailable?: boolean;
  entryCsrfToken?: string;
  sessionCsrfToken?: string;
}>;
export type InvitationPage =
  | Readonly<{ kind: 'landing'; locale: string; csrfToken: string; invalid?: boolean }>
  | (InvitationConfirmation &
      Readonly<{ kind: 'confirm'; locale: string; signinCsrfToken?: string }>);
