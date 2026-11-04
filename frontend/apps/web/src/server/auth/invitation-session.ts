import type { NativeInvitationInspection } from './native-invitation-contract.js';
import type { LoginStart, SessionCompletion } from './session-contract.js';

/** A projection only. The invitation code remains exclusively inside its native owner. */
export type InvitationContinuation = Readonly<{
  csrfToken: string;
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
  inspection?: NativeInvitationInspection;
  disposition?: 'created' | 'already_member';
  sessionAvailable?: boolean;
  entryCsrfToken?: string;
  sessionCsrfToken?: string;
  login?: LoginStart;
  completion?: SessionCompletion;
  opening?: InvitationOpening;
}>;
export type InvitationOpening = Readonly<{ handle: string; browser: string; csrfToken: string }>;
export type InvitationCommand =
  | Readonly<{ kind: 'capture'; csrf: string; code: string }>
  | Readonly<{ kind: 'inspect' | 'verify' | 'result' | 'cancel'; csrf: string }>
  | Readonly<{ kind: 'accept'; csrf: string; version: string }>;
export type InvitationResponseLease = Readonly<{
  signal: AbortSignal;
  cleaned: Promise<void>;
  pin: (assert: () => void) => void;
}>;
