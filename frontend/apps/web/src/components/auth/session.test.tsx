import { act, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { SessionControls } from './session';
import { extendBffSession } from '../../client/bff-session';
import en from '../../i18n/messages/en/auth.json';

vi.mock('../../client/bff-session', () => ({ extendBffSession: vi.fn() }));
const initial = () => ({
  state: 'authenticated' as const,
  displayName: 'School',
  csrfToken: 'A'.repeat(43),
  expiresAt: Date.now() + 150_000,
  limitedByAbsolute: false,
});
afterEach(() => {
  vi.useRealTimers();
  vi.resetAllMocks();
});
const view = (value = initial()) =>
  render(
    <SessionControls initial={value} labels={en.session} locale="en">
      <p>Admitted account view</p>
    </SessionControls>,
  );
describe('session warning, truthful extension and safe expiry', () => {
  it('warns without stealing focus, then hides protected content at expiry without a polling touch', () => {
    vi.useFakeTimers();
    view();
    const button = screen.getByRole('button', { name: en.session.extend });
    button.focus();
    expect(screen.queryByText(en.session.warning)).toBeNull();
    act(() => {
      vi.advanceTimersByTime(31_000);
    });
    expect(screen.getByText(en.session.warning).parentElement).toHaveAttribute(
      'aria-live',
      'polite',
    );
    expect(document.activeElement).toBe(button);
    act(() => {
      vi.advanceTimersByTime(120_000);
    });
    expect(screen.queryByText('Admitted account view')).toBeNull();
    expect(document.activeElement).toBe(screen.getByRole('alert'));
    expect(screen.getByRole('link', { name: en.session.reauthenticate })).toHaveAttribute(
      'href',
      '/api/auth/signin',
    );
    expect(extendBffSession).not.toHaveBeenCalled();
  });
  it('joins one explicit extension, displays the actual later expiry and prevents double submission', async () => {
    const value = initial();
    let complete!: (value: ReturnType<typeof initial>) => void;
    vi.mocked(extendBffSession).mockReturnValue(
      new Promise((resolve) => {
        complete = resolve;
      }),
    );
    view(value);
    const button = screen.getByRole('button', { name: en.session.extend });
    fireEvent.click(button);
    fireEvent.click(button);
    expect(button).toBeDisabled();
    expect(button).toHaveAttribute('aria-busy', 'true');
    expect(extendBffSession).toHaveBeenCalledExactlyOnceWith(value.csrfToken);
    await act(async () => {
      complete({ ...value, expiresAt: value.expiresAt + 60_000 });
    });
    expect(screen.getByText(en.session.extended)).toBeInTheDocument();
    expect(document.querySelector('time')).toHaveAttribute(
      'datetime',
      new Date(value.expiresAt + 60_000).toISOString(),
    );
  });
  it('does not claim extension if the provider deadline did not move', async () => {
    const value = initial();
    vi.mocked(extendBffSession).mockResolvedValue(value);
    view(value);
    screen.getByRole('button').focus();
    await act(async () => {
      fireEvent.click(screen.getByRole('button'));
    });
    expect(screen.getByText(en.session.unchanged)).toBeInTheDocument();
    expect(screen.queryByText(en.session.extended)).toBeNull();
    expect(screen.queryByRole('button')).toBeNull();
    expect(screen.getByRole('status')).toHaveTextContent(en.session.unchanged);
    expect(document.activeElement).toBe(screen.getByRole('status'));
  });
  it('announces and focuses the maximum notice when a completed extension removes its button', async () => {
    const value = initial();
    vi.mocked(extendBffSession).mockResolvedValue({
      ...value,
      expiresAt: value.expiresAt + 60_000,
      limitedByAbsolute: true,
    });
    view(value);
    screen.getByRole('button').focus();
    await act(async () => {
      fireEvent.click(screen.getByRole('button'));
    });
    expect(screen.getByRole('status')).toHaveTextContent(en.session.maximum);
    expect(document.activeElement).toBe(screen.getByRole('status'));
    expect(screen.queryByRole('button')).toBeNull();
  });
  it.each(['expiry', 'unchanged'] as const)(
    'does not steal external focus on %s recovery',
    async (kind) => {
      vi.useFakeTimers();
      const value = initial();
      vi.mocked(extendBffSession).mockResolvedValue(value);
      render(<button type="button">Outside session</button>);
      view(value);
      const outside = screen.getByRole('button', { name: 'Outside session' });
      outside.focus();
      if (kind === 'expiry')
        act(() => {
          vi.advanceTimersByTime(151_000);
        });
      else
        await act(async () => {
          fireEvent.click(screen.getByRole('button', { name: en.session.extend }));
        });
      expect(screen.getByRole(kind === 'expiry' ? 'alert' : 'status')).toBeInTheDocument();
      expect(document.activeElement).toBe(outside);
    },
  );
  it('offers reauthentication rather than extension at the known absolute maximum', () => {
    view({ ...initial(), limitedByAbsolute: true });
    expect(screen.getByText(en.session.maximum)).toBeInTheDocument();
    expect(screen.queryByRole('button')).toBeNull();
  });
  it('accepts a newly admitted RSC projection and ignores an older in-flight UI result', async () => {
    const first = initial();
    let complete!: (value: ReturnType<typeof initial>) => void;
    vi.mocked(extendBffSession).mockReturnValue(
      new Promise((resolve) => {
        complete = resolve;
      }),
    );
    const { rerender } = view(first);
    fireEvent.click(screen.getByRole('button'));
    const fresh = { ...first, expiresAt: first.expiresAt + 120_000 };
    rerender(
      <SessionControls initial={fresh} labels={en.session} locale="en">
        <p>Fresh account view</p>
      </SessionControls>,
    );
    await act(async () => {
      complete(first);
    });
    expect(document.querySelector('time')).toHaveAttribute(
      'datetime',
      new Date(fresh.expiresAt).toISOString(),
    );
    expect(screen.getByText('Fresh account view')).toBeInTheDocument();
    expect(screen.queryByText(en.session.unchanged)).toBeNull();
  });
  it('hides account content and focuses an accessible recovery notice after an explicit extension failure', async () => {
    vi.mocked(extendBffSession).mockRejectedValue(new Error('private-provider-canary'));
    view();
    await act(async () => {
      fireEvent.click(screen.getByRole('button'));
    });
    const alert = screen.getByRole('alert');
    expect(document.activeElement).toBe(alert);
    expect(screen.queryByText('Admitted account view')).toBeNull();
    expect(document.body.textContent).not.toContain('private-provider-canary');
  });
});
