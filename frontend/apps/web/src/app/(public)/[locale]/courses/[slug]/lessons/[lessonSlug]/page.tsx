import { notFound } from 'next/navigation';

import { PublicLesson } from '@/components/public/lesson';
import { PublicState } from '@/components/public/state';
import { publicMetadata } from '@/server/public-metadata';
import { requirePublicResource } from '@/server/public-resource';
import { getPublicUi } from '@/server/public-ui';

export async function generateMetadata() {
  const resource = await requirePublicResource();
  return publicMetadata(resource, await getPublicUi());
}

export default async function LessonPage() {
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
  if (resource.kind !== 'lesson') notFound();
  return <PublicLesson resource={resource} ui={ui} />;
}
