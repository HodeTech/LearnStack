import { describe, expect, it } from 'vitest';

import { BffInputError } from './input-error';
import { bffCursor, bffPageQuery, bffQuery } from './page-query';

describe('browser-safe canonical cursor grammar', () => {
  it.each([
    ['one symbol', 'A'],
    ['five symbols', 'AAAAA'],
    ['nonzero four trailing bits', 'AB'],
    ['nonzero two trailing bits', 'AAB'],
    ['URL alphabet with nonzero four trailing bits', '_x'],
    ['URL alphabet with nonzero two trailing bits', '__9'],
    ['empty', ''],
    ['over the 1024-character bound', 'A'.repeat(1025)],
    ['padding', 'YQ='],
    ['standard-base64 slash', '/w'],
    ['whitespace', 'YQ '],
  ])('rejects %s before page selection', (_label, value) => {
    expect(() => bffCursor(value)).toThrow(BffInputError);
    expect(() => bffPageQuery(new URLSearchParams({ cursor: value, limit: '20' }))).toThrow(
      BffInputError,
    );
  });

  it.each(['AA', 'AAA', 'AAAA', 'AQ', '_w', '__8', '-_8', 'AAAAAA', 'A'.repeat(1024)])(
    'preserves a canonical opaque cursor unchanged',
    (value) => {
      expect(bffCursor(value)).toBe(value);
      expect(bffPageQuery(new URLSearchParams({ cursor: value, limit: '100' }))).toEqual({
        cursor: value,
        limit: 100,
      });
    },
  );

  it.each([
    ['A', 'AQgw'],
    ['AA', 'AEIMQUYcgkosw048'],
    ['AAA', 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_'],
  ])('checks every possible terminal symbol after %s', (prefix, allowed) => {
    for (const suffix of 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_') {
      const value = prefix + suffix;
      if (allowed.includes(suffix)) expect(bffCursor(value)).toBe(value);
      else expect(() => bffCursor(value)).toThrow(BffInputError);
    }
  });

  it('checks decoded cursor spelling and preserves duplicate-field refusal', () => {
    expect(() =>
      bffPageQuery(bffQuery('/studio/members?cursor=%41%42&limit=20', ['cursor', 'limit'])),
    ).toThrow(BffInputError);
    expect(() => bffQuery('/studio/members?cursor=YQ&cur%73or=YQ', ['cursor', 'limit'])).toThrow(
      BffInputError,
    );
    expect(
      bffPageQuery(bffQuery('/studio/members?cursor=%59%51&limit=20', ['cursor', 'limit'])),
    ).toEqual({
      cursor: 'YQ',
      limit: 20,
    });
    expect(bffCursor(null)).toBeUndefined();
  });
});
