import { notFound } from 'next/navigation';

import { PublicState } from '@/components/public/state';
import { assertPublicRequestActive } from '@/server/public-request';
import { requirePublicResource } from '@/server/public-resource';
import { getPublicUi } from '@/server/public-ui';

export default async function MissingPage() {
  const resource = await requirePublicResource();
  if (resource.kind !== 'status') notFound();
  const ui = await getPublicUi();
  assertPublicRequestActive(resource.request);
  return (
    <PublicState
      state="missing"
      recoveryPath={`/${resource.request.locale}/courses`}
      locale={ui.locale}
      direction={ui.direction}
      t={ui.t}
    />
  );
}
