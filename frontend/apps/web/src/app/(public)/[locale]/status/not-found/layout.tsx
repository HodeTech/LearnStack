import type { Metadata } from 'next';
import { notFound } from 'next/navigation';
import type { ReactNode } from 'react';

import { assertPublicRequestActive } from '@/server/public-request';
import { requirePublicResource } from '@/server/public-resource';
import { getPublicUi } from '@/server/public-ui';

// Next's 404 metadata convention retains layout metadata and skips page metadata.
export async function generateMetadata(): Promise<Metadata> {
  const resource = await requirePublicResource();
  if (resource.kind !== 'status') notFound();
  const { t } = await getPublicUi();
  assertPublicRequestActive(resource.request);
  return {
    title: t('page.missing.title'),
    description: t('page.missing.description'),
    robots: { index: false, follow: false },
  };
}

export default function MissingLayout({ children }: { children: ReactNode }) {
  return children;
}
