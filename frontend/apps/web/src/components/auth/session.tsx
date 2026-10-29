'use client';

/* eslint-disable @next/next/no-html-link-for-pages -- Full document navigation prevents protected Router Cache reuse (ADR-0070). */

import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';

import { extendBffSession } from '@/client/bff-session';
import type { NativeReturnPath } from '@/server/auth/session-contract';
import type { SessionProjection } from '@/server/auth/session-projection';

export type SessionLabels = Readonly<{
  warning: string;
  until: string;
  extend: string;
  extending: string;
  extended: string;
  unchanged: string;
  maximum: string;
  failed: string;
  expired: string;
  reauthenticate: string;
  retry: string;
  javascript_required: string;
  unsaved: string;
}>;

/** Only a safe projection and already translated labels cross this client boundary. */
export function SessionControls({
  initial,
  labels,
  children,
  locale,
  returnTo,
  recoveryActions,
}: {
  readonly initial: SessionProjection;
  readonly locale: string;
  readonly labels: SessionLabels;
  readonly children: ReactNode;
  readonly returnTo: NativeReturnPath;
  readonly recoveryActions?: ReactNode;
}) {
  const [session, setSession] = useState(initial);
  // The server already admitted this projection. The first client render must
  // match its markup; evaluate the display clock after hydration, never as authority.
  const [now, setNow] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const [interactive, setInteractive] = useState(false);
  const [outcome, setOutcome] = useState<'extended' | 'unchanged' | 'failed' | null>(null);
  const [failure, setFailure] = useState(0);
  const [dirty, setDirty] = useState(false);
  const inFlight = useRef(false);
  const alive = useRef(true);
  const generation = useRef(0);
  const container = useRef<HTMLDivElement>(null);
  const alert = useRef<HTMLParagraphElement>(null);
  const status = useRef<HTMLParagraphElement>(null);
  const extendButton = useRef<HTMLButtonElement>(null);
  const expired = now !== null && now >= session.expiresAt;
  const ended = expired || outcome === 'failed';
  const recovery = session.limitedByAbsolute || outcome === 'unchanged';
  const previousView = useRef({ ended, recovery, failure });
  useEffect(() => {
    ++generation.current;
    setSession(initial);
    setNow(Date.now());
    setOutcome(null);
    setFailure(0);
  }, [initial]);
  useEffect(() => {
    alive.current = true;
    setInteractive(true);
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => {
      alive.current = false;
      clearInterval(timer);
    };
  }, []);
  useEffect(() => {
    const root = container.current;
    setDirty(false);
    if (!root || ended) return;
    // Keep only DOM identities: field values stay in their native controls.
    // Replacing the admitted content discards this set and every listener.
    const forms = new Set<HTMLFormElement>();
    let listening = false;
    const beforeUnload = (event: BeforeUnloadEvent) => {
      if (forms.size === 0) return;
      event.preventDefault();
      event.returnValue = '';
    };
    const synchronize = () => {
      const changed = forms.size > 0;
      setDirty(changed);
      if (changed && !listening) window.addEventListener('beforeunload', beforeUnload);
      if (!changed && listening) window.removeEventListener('beforeunload', beforeUnload);
      listening = changed;
    };
    const edit = (event: Event) => {
      const control = event.target;
      const editable =
        control instanceof HTMLSelectElement
          ? !control.disabled
          : control instanceof HTMLTextAreaElement
            ? !control.disabled && !control.readOnly
            : control instanceof HTMLInputElement &&
              !control.disabled &&
              !control.readOnly &&
              !['hidden', 'button', 'submit', 'reset', 'image'].includes(control.type);
      if (!editable) return;
      const form = (control as HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement).form;
      if (!form || !root.contains(form)) return;
      forms.add(form);
      synchronize();
    };
    const submit = (event: Event) => {
      if (
        !event.defaultPrevented &&
        event.target instanceof HTMLFormElement &&
        forms.delete(event.target)
      )
        synchronize();
    };
    root.addEventListener('input', edit);
    root.addEventListener('change', edit);
    root.addEventListener('submit', submit);
    return () => {
      root.removeEventListener('input', edit);
      root.removeEventListener('change', edit);
      root.removeEventListener('submit', submit);
      window.removeEventListener('beforeunload', beforeUnload);
      forms.clear();
    };
  }, [initial, children, ended]);
  useEffect(() => {
    const changed =
      ended !== previousView.current.ended ||
      (ended && failure !== previousView.current.failure) ||
      (recovery && !previousView.current.recovery);
    previousView.current = { ended, recovery, failure };
    if (!changed) return;
    // A removed focused descendant falls back to body. Recover that focus, but
    // never interrupt a user who moved to another part of the document.
    const focused = document.activeElement;
    if (focused !== document.body && !container.current?.contains(focused)) return;
    (ended ? alert.current : recovery ? status.current : extendButton.current)?.focus();
  }, [ended, recovery, failure]);
  const warning = !ended && now !== null && session.expiresAt - now <= 120_000;
  const signin = '/api/auth/signin?returnTo=' + encodeURIComponent(returnTo);
  async function extend() {
    if (inFlight.current) return;
    inFlight.current = true;
    setBusy(true);
    // A failed check withholds protected content until a later positive result.
    // Starting a retry must not briefly restore the previous projection.
    setOutcome((previous) => (previous === 'failed' ? previous : null));
    const current = generation.current;
    try {
      const updated = await extendBffSession(session.csrfToken);
      if (!alive.current || current !== generation.current) return;
      setOutcome(updated.expiresAt > session.expiresAt ? 'extended' : 'unchanged');
      setSession(updated);
      setNow(Date.now());
    } catch {
      if (alive.current && current === generation.current) {
        setOutcome('failed');
        setFailure((previous) => previous + 1);
      }
    } finally {
      inFlight.current = false;
      if (alive.current) setBusy(false);
    }
  }
  return (
    <div data-bff-protected="true" ref={container}>
      <div aria-live="polite" aria-atomic="true">
        {warning && <p>{labels.warning}</p>}
        {busy && <p>{labels.extending}</p>}
        {!ended && !warning && !busy && outcome === 'extended' && <p>{labels.extended}</p>}
        {!ended && dirty && <p>{labels.unsaved}</p>}
      </div>
      <p>
        {labels.until}:{' '}
        <time dateTime={new Date(session.expiresAt).toISOString()}>
          {new Intl.DateTimeFormat(locale, {
            timeZone: 'UTC',
            dateStyle: 'medium',
            timeStyle: 'long',
          }).format(session.expiresAt)}
        </time>
      </p>
      {ended ? (
        <>
          <p key={failure} role="alert" tabIndex={-1} ref={alert}>
            {expired ? labels.expired : labels.failed} <a href={signin}>{labels.reauthenticate}</a>
          </p>
          {interactive && !session.limitedByAbsolute && (
            <button type="button" onClick={extend} aria-disabled={busy} aria-busy={busy}>
              {busy ? labels.extending : labels.retry}
            </button>
          )}
        </>
      ) : (
        <>
          {recovery ? (
            <p role="status" aria-atomic="true" tabIndex={-1} ref={status}>
              {session.limitedByAbsolute ? labels.maximum : labels.unchanged}{' '}
              <a href={signin}>{labels.reauthenticate}</a>
            </p>
          ) : (
            interactive && (
              <button
                ref={extendButton}
                type="button"
                onClick={extend}
                aria-disabled={busy}
                aria-busy={busy}
              >
                {busy ? labels.extending : labels.extend}
              </button>
            )
          )}
          {children}
        </>
      )}
      {recoveryActions}
      <noscript>
        <p>{labels.javascript_required}</p>
        <a href={signin}>{labels.reauthenticate}</a>
      </noscript>
    </div>
  );
}
