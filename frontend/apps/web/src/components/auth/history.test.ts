import { describe, expect, it, vi } from 'vitest';

import { BFF_HISTORY_SOURCE } from './history';

describe('fixed protected history lifecycle', () => {
  it('veils before caching and forces a fresh navigation on restore without exposing the old document', () => {
    const listeners = new Map<string, (event: { persisted?: boolean }) => void>();
    const style = { visibility: '' };
    const reload = vi.fn();
    // Test-only execution of the exact fixed script; no request/user input is evaluated.
    const install = new Function('addEventListener', 'document', 'location', BFF_HISTORY_SOURCE);
    install(
      (event: string, callback: (event: { persisted?: boolean }) => void) =>
        listeners.set(event, callback),
      { documentElement: { style, getAttribute: () => null } },
      { reload },
    );
    listeners.get('pageshow')!({ persisted: false });
    expect(style.visibility).toBe('');
    expect(reload).not.toHaveBeenCalled();
    listeners.get('pagehide')!({});
    expect(style.visibility).toBe('hidden');
    listeners.get('pageshow')!({ persisted: true });
    expect(style.visibility).toBe('hidden');
    expect(reload).toHaveBeenCalledOnce();
  });
  it.each([
    ['/portal/profile', '/portal/profile'],
    [
      '/studio/members/019a154b-0000-7000-8000-000000000011',
      '/studio/members/019a154b-0000-7000-8000-000000000011',
    ],
    ['/api/auth/entry', '/api/auth/entry'],
    ['/api/auth/signin', '/api/auth/signin'],
    ['/api/account/profile', '/api/auth/signin'],
    ['/api/auth/entry/create', '/api/auth/signin'],
    ['//other.example', '/api/auth/signin'],
    ['/portal/profile?redirect=other', '/api/auth/signin'],
    ['/studio/members/019A154B-0000-7000-8000-000000000011', '/api/auth/signin'],
    ['', '/api/auth/signin'],
  ])('recovers a native POST result with %s only through a checked GET', (attribute, expected) => {
    const listeners = new Map<string, (event: { persisted?: boolean }) => void>();
    const style = { visibility: '' };
    const replace = vi.fn();
    const reload = vi.fn();
    const install = new Function('addEventListener', 'document', 'location', BFF_HISTORY_SOURCE);
    install(
      (event: string, callback: (event: { persisted?: boolean }) => void) =>
        listeners.set(event, callback),
      {
        documentElement: {
          style,
          getAttribute: (name: string) => (name === 'data-bff-history-recovery' ? attribute : null),
        },
      },
      { replace, reload },
    );
    listeners.get('pageshow')!({ persisted: false });
    expect(replace).not.toHaveBeenCalled();
    listeners.get('pagehide')!({});
    expect(style.visibility).toBe('hidden');
    listeners.get('pageshow')!({ persisted: false });
    expect(style.visibility).toBe('hidden');
    expect(replace).toHaveBeenCalledExactlyOnceWith(expected);
    expect(reload).not.toHaveBeenCalled();
  });
});
