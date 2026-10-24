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
      { documentElement: { style } },
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
});
