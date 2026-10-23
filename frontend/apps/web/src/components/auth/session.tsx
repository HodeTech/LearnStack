'use client';

/* eslint-disable @next/next/no-html-link-for-pages -- Full document navigation prevents protected Router Cache reuse (ADR-0070). */

import { useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';

import { extendBffSession } from '@/client/bff-session';
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
  reauthenticate: string;
}>;

/** Only a safe projection and already translated labels cross this client boundary. */
export function SessionControls({
  initial,
  labels,
  children,
  locale,
}: {
  readonly initial: SessionProjection;
  readonly locale: string;
  readonly labels: SessionLabels;
  readonly children: ReactNode;
}) {
  const [session, setSession] = useState(initial);
  const [now, setNow] = useState(() => Date.now());
  const [busy, setBusy] = useState(false);
  const [outcome, setOutcome] = useState<'extended' | 'unchanged' | 'failed' | null>(null);
  const inFlight = useRef(false);
  const alive = useRef(true);
  const generation = useRef(0);
  const container = useRef<HTMLDivElement>(null);
  const alert = useRef<HTMLParagraphElement>(null);
  const status = useRef<HTMLParagraphElement>(null);
  const expired = now >= session.expiresAt;
  const ended = expired || outcome === 'failed';
  const recovery = session.limitedByAbsolute || outcome === 'unchanged';
  const previousView = useRef({ ended, recovery });
  useEffect(() => {
    ++generation.current;
    setSession(initial);
    setNow(Date.now());
    setOutcome(null);
  }, [initial]);
  useEffect(() => {
    alive.current = true;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => {
      alive.current = false;
      clearInterval(timer);
    };
  }, []);
  useEffect(() => {
    const changed =
      (ended && !previousView.current.ended) || (recovery && !previousView.current.recovery);
    previousView.current = { ended, recovery };
    if (!changed) return;
    // A removed focused descendant falls back to body. Recover that focus, but
    // never interrupt a user who moved to another part of the document.
    const focused = document.activeElement;
    if (focused !== document.body && !container.current?.contains(focused)) return;
    (ended ? alert.current : status.current)?.focus();
  }, [ended, recovery]);
  const warning = session.expiresAt - now <= 120_000;
  async function extend() {
    if (inFlight.current) return;
    inFlight.current = true;
    setBusy(true);
    setOutcome(null);
    const current = generation.current;
    try {
      const updated = await extendBffSession(session.csrfToken);
      if (!alive.current || current !== generation.current) return;
      setOutcome(updated.expiresAt > session.expiresAt ? 'extended' : 'unchanged');
      setSession(updated);
      setNow(Date.now());
    } catch {
      if (alive.current && current === generation.current) setOutcome('failed');
    } finally {
      inFlight.current = false;
      if (alive.current) setBusy(false);
    }
  }
  return (
    <div data-bff-protected="true" ref={container}>
      <div aria-live="polite" aria-atomic="true">
        {warning && <p>{labels.warning}</p>}
        {outcome === 'extended' && <p>{labels.extended}</p>}
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
        <p role="alert" tabIndex={-1} ref={alert}>
          {labels.failed} <a href="/api/auth/signin">{labels.reauthenticate}</a>
        </p>
      ) : (
        <>
          {recovery ? (
            <p role="status" aria-atomic="true" tabIndex={-1} ref={status}>
              {session.limitedByAbsolute ? labels.maximum : labels.unchanged}{' '}
              <a href="/api/auth/signin">{labels.reauthenticate}</a>
            </p>
          ) : (
            <button type="button" onClick={extend} disabled={busy} aria-busy={busy}>
              {busy ? labels.extending : labels.extend}
            </button>
          )}
          {children}
        </>
      )}
    </div>
  );
}
