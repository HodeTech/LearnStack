import type { VerifiedLogoutToken } from './backchannel-contract.js';

const CAPACITY = 4096;
const SKEW_MS = 30_000;
const HORIZON_MS = 300_000;
export type ProviderLogoutTarget = Readonly<{ issuer: string; sid: string; subject: string }>;
type Fence = Readonly<{
  issuer: string;
  sid: string;
  subject: string | null;
  sequence: number;
  horizon: number;
  acceptedAt: number;
}>;
export class BackchannelStateError extends Error {
  constructor(readonly reason: 'invalid' | 'capacity' | 'clock' | 'stopped') {
    super('Native back-channel notification refused');
    this.name = 'BackchannelStateError';
  }
}
export const matchesProviderLogout = (
  target: ProviderLogoutTarget,
  fence: Pick<Fence, 'issuer' | 'sid' | 'subject'>,
) =>
  target.issuer === fence.issuer &&
  target.sid === fence.sid &&
  (fence.subject === null || target.subject === fence.subject);

/** Native-only replay and publication evidence. No raw token is retained. */
export class BackchannelState {
  readonly #replay = new Map<string, Readonly<{ fingerprint: string; deadline: number }>>();
  readonly #fences = new Map<string, Fence>();
  #last: Readonly<{ mono: number; utc: number }>;
  readonly #anchor: Readonly<{ mono: number; utc: number }>;
  #utcOffset: number;
  #unsafe = false;
  #stopped = false;
  #sequence = 0;
  constructor(readonly clock: { monotonic(): number; utc(): number }) {
    this.#last = this.#readClock();
    this.#anchor = this.#last;
    this.#utcOffset = this.#last.utc - this.#last.mono;
    this.#unsafe = !this.#validClock(this.#last);
  }
  #validClock(sample: { mono: number; utc: number }) {
    return Number.isFinite(sample.mono) && Number.isSafeInteger(sample.utc);
  }
  #readClock() {
    try {
      return { mono: this.clock.monotonic(), utc: this.clock.utc() };
    } catch {
      return { mono: Number.NaN, utc: Number.NaN };
    }
  }
  #sample() {
    const sample = this.#readClock();
    if (
      !this.#validClock(sample) ||
      sample.mono < this.#last.mono ||
      Math.abs(sample.utc - this.#anchor.utc - (sample.mono - this.#anchor.mono)) > SKEW_MS
    )
      this.#unsafe = true;
    if (!this.#unsafe) {
      this.#last = sample;
      // A small UTC correction must never reopen an already retired token window.
      this.#utcOffset = Math.max(this.#utcOffset, sample.utc - sample.mono);
    }
    return { mono: sample.mono, utc: Math.max(sample.utc, sample.mono + this.#utcOffset) };
  }
  #key(target: Pick<Fence, 'issuer' | 'sid' | 'subject'>) {
    return JSON.stringify([target.issuer, target.sid, target.subject]);
  }
  sweep(needed: (fence: Fence) => boolean) {
    const sample = this.#sample();
    if (this.#unsafe && !this.#stopped) return;
    for (const [key, record] of this.#replay)
      if (this.#stopped || sample.mono > record.deadline) this.#replay.delete(key);
    for (const [key, fence] of this.#fences)
      if ((this.#stopped || sample.mono > fence.horizon) && !needed(fence))
        this.#fences.delete(key);
  }
  accepts(target: ProviderLogoutTarget) {
    return ![...this.#fences.values()].some((fence) => matchesProviderLogout(target, fence));
  }
  apply(token: VerifiedLogoutToken, close: () => void): 'accepted' | 'duplicate' {
    if (this.#stopped) throw new BackchannelStateError('stopped');
    const { mono, utc } = this.#sample();
    if (this.#unsafe) throw new BackchannelStateError('clock');
    const opaque = (value: unknown): value is string =>
      typeof value === 'string' &&
      value.length > 0 &&
      Buffer.byteLength(value) <= 255 &&
      // eslint-disable-next-line no-control-regex -- The fixed OIDC opaque identifier profile rejects controls.
      !/[\u0000-\u001f\u007f-\u009f\ud800-\udfff]/u.test(value);
    if (
      !token ||
      typeof token.issuer !== 'string' ||
      !token.issuer ||
      token.issuer.length > 2048 ||
      !opaque(token.sid) ||
      !opaque(token.jti) ||
      (token.subject !== null &&
        (typeof token.subject !== 'string' ||
          !token.subject ||
          token.subject.length > 255 ||
          /^[\s\u0085]+$/u.test(token.subject) ||
          /[\0\r\n\ud800-\udfff]/u.test(token.subject))) ||
      typeof token.fingerprint !== 'string' ||
      !/^[a-f0-9]{64}$/.test(token.fingerprint) ||
      !Number.isSafeInteger(token.issuedAt) ||
      !Number.isSafeInteger(token.expiresAt) ||
      token.expiresAt <= token.issuedAt ||
      token.expiresAt - token.issuedAt > HORIZON_MS ||
      token.issuedAt > utc + SKEW_MS ||
      token.issuedAt < utc - HORIZON_MS ||
      token.expiresAt < utc - SKEW_MS
    )
      throw new BackchannelStateError('invalid');
    const replayKey = JSON.stringify([token.issuer, token.jti]);
    const replay = this.#replay.get(replayKey);
    if (replay) {
      if (replay.fingerprint !== token.fingerprint) throw new BackchannelStateError('invalid');
      return 'duplicate';
    }
    const fenceKey = this.#key(token);
    const previous = this.#fences.get(fenceKey);
    if (this.#replay.size >= CAPACITY || (!previous && this.#fences.size >= CAPACITY))
      throw new BackchannelStateError('capacity');
    // Both reservations precede gate closure; capacity refusal has no partial effects.
    this.#replay.set(replayKey, {
      fingerprint: token.fingerprint,
      deadline: mono + Math.max(0, token.expiresAt + SKEW_MS - utc),
    });
    this.#fences.set(
      fenceKey,
      Object.freeze({
        issuer: token.issuer,
        sid: token.sid,
        subject: token.subject,
        sequence: ++this.#sequence,
        acceptedAt: previous?.acceptedAt ?? mono,
        horizon: Math.max(
          previous?.horizon ?? 0,
          mono + HORIZON_MS,
          mono + Math.max(0, token.expiresAt + SKEW_MS - utc),
        ),
      }),
    );
    close();
    return 'accepted';
  }
  stop() {
    this.#stopped = true;
  }
  counts() {
    const sample = this.#sample();
    const mono = Number.isFinite(sample.mono)
      ? sample.mono
      : Number.isFinite(this.#last.mono)
        ? this.#last.mono
        : 0;
    const oldest = Math.min(mono, ...[...this.#fences.values()].map((fence) => fence.acceptedAt));
    return Object.freeze({
      oldestHeldAgeMs: Math.max(0, mono - oldest),
      replay: this.#replay.size,
      fences: this.#fences.size,
      unavailable: this.#unsafe || this.#stopped,
      saturated: this.#replay.size >= CAPACITY || this.#fences.size >= CAPACITY,
    });
  }
}
