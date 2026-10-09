import { notFound } from 'next/navigation';

import { PublicCatalog } from '@/components/public/catalog';
import { PublicState } from '@/components/public/state';
import { publicMetadata } from '@/server/public-metadata';
import { requirePublicResource } from '@/server/public-resource';
import { getPublicUi } from '@/server/public-ui';

export async function generateMetadata() {
  const resource = await requirePublicResource();
  return publicMetadata(resource, await getPublicUi());
}

export default async function CoursesPage() {
  const resource = await requirePublicResource();
  const ui = await getPublicUi();
  if (resource.kind === 'failure')
    return (
      <PublicState
        state={resource.state}
        recoveryPath={resource.request.route.path}
        locale={ui.locale}
        direction={ui.direction}
        t={ui.t}
      />
    );
  if (resource.kind !== 'catalog') notFound();
  return <PublicCatalog resource={resource} ui={ui} />;
}
