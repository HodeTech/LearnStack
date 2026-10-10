'use client';

import { useErrorLabels } from '@/components/public/error-labels';

export default function PublicError({
  reset,
}: {
  readonly error: Error & { digest?: string };
  readonly reset: () => void;
}) {
  const labels = useErrorLabels();
  if (!labels) return null;
  return (
    <section className="public-state" lang={labels.locale} dir={labels.direction}>
      <h1>{labels.title}</h1>
      <p className="public-muted">{labels.description}</p>
      <p>
        <button type="button" onClick={reset}>
          {labels.retry}
        </button>
      </p>
      <p>
        <a href={labels.recoveryPath}>{labels.recovery}</a>
      </p>
    </section>
  );
}
