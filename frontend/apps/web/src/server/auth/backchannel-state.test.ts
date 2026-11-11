// @vitest-environment node
import { createHash } from 'node:crypto';

import { describe, expect, it } from 'vitest';

import type { VerifiedLogoutToken } from './backchannel-contract';
import { BackchannelState, BackchannelStateError } from './backchannel-state';

function fixture() {
  let mono = 0;
  let utc = 1_800_000_000_000;
  let sequence = 0;
  const state = new BackchannelState({ monotonic: () => mono, utc: () => utc });
  const token = (patch: Partial<VerifiedLogoutToken> = {}): VerifiedLogoutToken => {
    const jti = 'notification-' + ++sequence;
    return {
      issuer: 'https://identity.example.test/realms/learnstack',
      sid: 'provider-session',
      subject: 'opaque-subject',
      jti,
      fingerprint: createHash('sha256').update(jti).digest('hex'),
      issuedAt: utc,
      expiresAt: utc + 120_000,
      ...patch,
    };
  };
  return {
    state,
    token,
    advance: (ms: number) => {
      mono += ms;
      utc += ms;
    },
    moveMono: (ms: number) => {
      mono += ms;
    },
    moveUtc: (ms: number) => {
      utc += ms;
    },
  };
}
const noClosure = () => undefined;

describe('bounded native logout evidence', () => {
  it('reserves replay and fences before executing the closure transition', () => {
    const f = fixture();
    const token = f.token();
    let count = 0;
    f.state.apply(token, () => {
      count++;
      expect(f.state.counts()).toMatchObject({ replay: 1, fences: 1 });
      expect(
        f.state.accepts({ issuer: token.issuer, sid: token.sid, subject: token.subject! }),
      ).toBe(false);
    });
    expect(
      f.state.apply(token, () => {
        count++;
      }),
    ).toBe('duplicate');
    expect(count).toBe(1);
  });

  it('refuses replay saturation without reserving a fence or closing any owner', () => {
    const f = fixture();
    for (let i = 0; i < 4096; i++) f.state.apply(f.token(), noClosure);
    let closed = false;
    expect(() =>
      f.state.apply(f.token({ sid: 'different-target' }), () => {
        closed = true;
      }),
    ).toThrow(BackchannelStateError);
    expect(closed).toBe(false);
    expect(f.state.counts()).toMatchObject({ replay: 4096, fences: 1, saturated: true });
  });

  it('refuses fence saturation atomically after replay records expire', () => {
    const f = fixture();
    for (let i = 0; i < 4096; i++) f.state.apply(f.token({ sid: 'target-' + i }), noClosure);
    f.advance(150_001);
    f.state.sweep(() => false);
    expect(f.state.counts()).toMatchObject({ replay: 0, fences: 4096 });
    let closed = false;
    expect(() =>
      f.state.apply(f.token({ sid: 'unreserved-target' }), () => {
        closed = true;
      }),
    ).toThrow(BackchannelStateError);
    expect(closed).toBe(false);
    expect(f.state.counts().replay).toBe(0);
    f.state.apply(f.token({ sid: 'target-0' }), noClosure);
    expect(f.state.counts()).toMatchObject({ replay: 1, fences: 4096 });
  });

  it('never evicts needed fences and extends the same target horizon', () => {
    const f = fixture();
    f.state.apply(f.token(), noClosure);
    f.advance(250_000);
    f.state.sweep(() => false);
    f.state.apply(f.token(), noClosure);
    f.advance(100_000);
    f.state.sweep(() => false);
    expect(f.state.counts().fences).toBe(1);
    f.advance(201_000);
    f.state.sweep(() => true);
    expect(f.state.counts()).toMatchObject({ fences: 1, oldestHeldAgeMs: 551_000 });
    f.state.sweep(() => false);
    expect(f.state.counts().fences).toBe(0);
  });

  it('keeps the expiration-skew boundary and forgets only after it', () => {
    const f = fixture();
    const token = f.token();
    f.state.apply(token, noClosure);
    f.advance(150_000);
    f.state.sweep(() => false);
    expect(f.state.apply(token, noClosure)).toBe('duplicate');
    f.advance(1);
    f.state.sweep(() => false);
    expect(f.state.counts().replay).toBe(0);
    expect(() => f.state.apply(token, noClosure)).toThrow(BackchannelStateError);
    f.advance(150_000);
    f.state.sweep(() => false);
    expect(f.state.counts().fences).toBe(0);
  });

  it('rejects a retired token even after a small backward UTC correction', () => {
    const f = fixture();
    const token = f.token();
    f.state.apply(token, noClosure);
    f.advance(150_001);
    f.state.sweep(() => false);
    f.moveUtc(-20_000);
    expect(() => f.state.apply(token, noClosure)).toThrow(BackchannelStateError);
    expect(f.state.counts().replay).toBe(0);
  });

  it.each([
    'backward UTC',
    'forward UTC',
    'backward monotonic',
    'forward monotonic',
    'cumulative UTC',
  ])('seals notification admission on unsafe %s movement without shortening evidence', (kind) => {
    const f = fixture();
    const token = f.token();
    f.state.apply(token, noClosure);
    if (kind === 'backward UTC') f.moveUtc(-31_000);
    if (kind === 'forward UTC') f.moveUtc(31_000);
    if (kind === 'backward monotonic') f.moveMono(-1);
    if (kind === 'forward monotonic') f.moveMono(31_000);
    if (kind === 'cumulative UTC') {
      f.moveUtc(20_000);
      f.state.sweep(() => false);
      f.moveUtc(20_000);
    }
    f.state.sweep(() => false);
    expect(() => f.state.apply(f.token(), noClosure)).toThrow(BackchannelStateError);
    f.advance(600_000);
    f.state.sweep(() => false);
    expect(f.state.counts()).toMatchObject({ replay: 1, fences: 1, unavailable: true });
  });

  it.each([
    { issuedAt: Number.NaN },
    { expiresAt: Number.POSITIVE_INFINITY },
    { sid: '' },
    { sid: 'x'.repeat(256) },
    { sid: 'bad\u0085sid' },
    { subject: '' },
    { subject: '\u3000' },
    { jti: '' },
    { fingerprint: 'raw-private-token' },
  ])('rejects malformed verified-boundary shape without changing evidence: %j', (patch) => {
    const f = fixture();
    expect(() => f.state.apply(f.token(patch), noClosure)).toThrow(BackchannelStateError);
    expect(f.state.counts()).toMatchObject({ replay: 0, fences: 0 });
  });

  it('releases stopped evidence after actual retirement even with an unsafe clock', () => {
    const f = fixture();
    f.state.apply(f.token(), noClosure);
    f.moveUtc(-31_000);
    f.state.sweep(() => true);
    f.state.stop();
    f.state.sweep(() => true);
    expect(f.state.counts()).toMatchObject({ replay: 0, fences: 1, unavailable: true });
    f.state.sweep(() => false);
    expect(f.state.counts().fences).toBe(0);
  });

  it('allows independent targets while retaining an unknown-target fence', () => {
    const f = fixture();
    const token = f.token();
    f.state.apply(token, noClosure);
    for (const target of [
      { issuer: token.issuer + '/other', sid: token.sid, subject: token.subject! },
      { issuer: token.issuer, sid: 'different-session', subject: token.subject! },
      { issuer: token.issuer, sid: token.sid, subject: 'different-subject' },
    ])
      expect(f.state.accepts(target)).toBe(true);
  });
});
