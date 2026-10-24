import { act, fireEvent, render, screen } from '@testing-library/react';
import { hydrateRoot } from 'react-dom/client';
import { renderToStaticMarkup, renderToString } from 'react-dom/server';
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
    <SessionControls
      initial={value}
      labels={en.session}
      locale="en"
      returnTo="/studio"
      recoveryActions={
        <form action="/api/auth/signout" method="post">
          <button>Sign out</button>
        </form>
      }
    >
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
    expect(screen.queryByText(en.session.warning)).toBeNull();
    expect(screen.getByRole('alert')).toHaveTextContent(en.session.expired);
    expect(screen.queryByText(en.session.failed)).toBeNull();
    expect(document.activeElement).toBe(screen.getByRole('alert'));
    expect(screen.getByRole('link', { name: en.session.reauthenticate })).toHaveAttribute(
      'href',
      '/api/auth/signin?returnTo=%2Fstudio',
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
    expect(button).toHaveAttribute('aria-disabled', 'true');
    expect(button).toBeEnabled();
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
    screen.getByRole('button', { name: en.session.extend }).focus();
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: en.session.extend }));
    });
    expect(screen.getByText(en.session.unchanged)).toBeInTheDocument();
    expect(screen.queryByText(en.session.extended)).toBeNull();
    expect(screen.queryByRole('button', { name: en.session.extend })).toBeNull();
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
    screen.getByRole('button', { name: en.session.extend }).focus();
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: en.session.extend }));
    });
    expect(screen.getByRole('status')).toHaveTextContent(en.session.maximum);
    expect(document.activeElement).toBe(screen.getByRole('status'));
    expect(screen.queryByRole('button', { name: en.session.extend })).toBeNull();
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
    expect(screen.queryByRole('button', { name: en.session.extend })).toBeNull();
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
    fireEvent.click(screen.getByRole('button', { name: en.session.extend }));
    const fresh = { ...first, expiresAt: first.expiresAt + 120_000 };
    rerender(
      <SessionControls initial={fresh} labels={en.session} locale="en" returnTo="/studio">
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
      fireEvent.click(screen.getByRole('button', { name: en.session.extend }));
    });
    const alert = screen.getByRole('alert');
    expect(document.activeElement).toBe(alert);
    expect(screen.queryByText('Admitted account view')).toBeNull();
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: en.session.retry })).toBeInTheDocument();
    expect(document.body.textContent).not.toContain('private-provider-canary');
  });
  it('keeps protected content hidden during recovery and restores it only after a positive extension', async () => {
    const value = initial();
    let complete!: (value: ReturnType<typeof initial>) => void;
    vi.mocked(extendBffSession)
      .mockRejectedValueOnce(new Error('unavailable'))
      .mockReturnValueOnce(
        new Promise((resolve) => {
          complete = resolve;
        }),
      );
    view(value);
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: en.session.extend }));
    });
    const retry = screen.getByRole('button', { name: en.session.retry });
    retry.focus();
    fireEvent.click(retry);
    expect(screen.queryByText('Admitted account view')).toBeNull();
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: en.session.extending })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
    await act(async () => {
      complete({ ...value, expiresAt: value.expiresAt + 60_000 });
    });
    expect(screen.getByText('Admitted account view')).toBeInTheDocument();
    expect(document.activeElement).toBe(screen.getByRole('button', { name: en.session.extend }));
  });
  it('announces and focuses a fresh notice after each failed retry', async () => {
    vi.mocked(extendBffSession).mockRejectedValue(new Error('unavailable'));
    view();
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: en.session.extend }));
    });
    const first = screen.getByRole('alert');
    screen.getByRole('button', { name: en.session.retry }).focus();
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: en.session.retry }));
    });
    expect(screen.getByRole('alert')).not.toBe(first);
    expect(document.activeElement).toBe(screen.getByRole('alert'));
    expect(screen.queryByText('Admitted account view')).toBeNull();
  });
  it('hydrates the admitted projection without a clock-dependent markup mismatch', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(2_000_000_000_000);
    const element = (
      <SessionControls initial={initial()} labels={en.session} locale="en" returnTo="/studio">
        <p>Admitted account view</p>
      </SessionControls>
    );
    const container = document.createElement('div');
    container.innerHTML = renderToString(element);
    document.body.append(container);
    vi.setSystemTime(2_000_000_200_000);
    const recoverable = vi.fn();
    let root: ReturnType<typeof hydrateRoot> | undefined;
    try {
      await act(async () => {
        root = hydrateRoot(container, element, { onRecoverableError: recoverable });
      });
      expect(recoverable).not.toHaveBeenCalled();
      expect(container.textContent).toContain(en.session.expired);
      expect(container.textContent).not.toContain('Admitted account view');
    } finally {
      await act(async () => root?.unmount());
      container.remove();
    }
  });
  it('drops the earlier success announcement when the next warning window begins', async () => {
    vi.useFakeTimers();
    const value = initial();
    vi.mocked(extendBffSession).mockResolvedValue({
      ...value,
      expiresAt: value.expiresAt + 60_000,
    });
    view(value);
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: en.session.extend }));
    });
    expect(screen.getByText(en.session.extended)).toBeInTheDocument();
    act(() => {
      vi.advanceTimersByTime(91_000);
    });
    expect(screen.getByText(en.session.warning)).toBeInTheDocument();
    expect(screen.queryByText(en.session.extended)).toBeNull();
  });
  it('renders a truthful non-JavaScript fallback without an inert extension button', () => {
    const html = renderToStaticMarkup(
      <SessionControls
        initial={initial()}
        labels={en.session}
        locale="en"
        returnTo="/portal"
        recoveryActions={
          <form action="/api/auth/signout" method="post">
            <button>Sign out</button>
          </form>
        }
      >
        <p>Admitted account view</p>
      </SessionControls>,
    );
    expect(html).not.toContain(en.session.extend);
    expect(html).toContain(en.session.javascript_required);
    expect(html).toContain('/api/auth/signin?returnTo=%2Fportal');
    expect(html).toContain('action="/api/auth/signout"');
  });
});
