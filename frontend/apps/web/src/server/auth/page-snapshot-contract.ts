import type { IngressContext } from '../ingress.js';
import type { NativeEntrySnapshot, NativePageSnapshot, PageSite } from './page-contract.js';

/** Native bootstrap injection only. The ticket's authority lives in an actual-frame WeakMap. */
export type NativeSnapshotTicket = Readonly<{
  binding: IngressContext;
  site: PageSite;
  signal: AbortSignal;
  activity: 'read' | 'passive';
  flight: boolean;
  prefetch: boolean;
}>;

export type NativePageLease = Readonly<{
  signal: AbortSignal;
  cleaned: Promise<void>;
  /** Reserved requests survive refresh; pinned requests retain their original revision. */
  pinned: boolean;
  pin: (validate: () => boolean, expiresInMs: number) => void;
  track: (actualWork: PromiseLike<unknown>) => void;
  publish: (page: NativePageSnapshot) => NativePageSnapshot;
  publishEntry: (entry: NativeEntrySnapshot) => NativeEntrySnapshot;
  abort: () => void;
}>;

export type NativePagePreparation = Readonly<{
  current: (binding: IngressContext) => NativeSnapshotTicket;
  reserve: (ticket: NativeSnapshotTicket, actualOwner: object) => NativePageLease;
}>;
