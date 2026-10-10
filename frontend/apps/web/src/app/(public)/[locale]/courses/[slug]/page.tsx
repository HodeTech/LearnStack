import { notFound } from 'next/navigation';

import { PublicCourse } from '@/components/public/course';
import { PublicState } from '@/components/public/state';
import { publicMetadata } from '@/server/public-metadata';
import { catalogPath } from '@/server/public-paths';
import { assertPublicRequestActive } from '@/server/public-request';
import { requirePublicResource } from '@/server/public-resource';
import { getPublicUi } from '@/server/public-ui';

export async function generateMetadata() {
  const resource = await requirePublicResource();
  const ui = await getPublicUi();
  assertPublicRequestActive(resource.request);
  return publicMetadata(resource, ui);
}

export default async function CoursePage() {
  const resource = await requirePublicResource();
  const ui = await getPublicUi();
  assertPublicRequestActive(resource.request);
  if (resource.kind === 'failure')
    return (
      <PublicState
        state={resource.state}
        recoveryPath={
          resource.state === 'invalid_cursor'
            ? resource.request.route.path
            : (catalogPath(resource.request.locale ?? '') ?? '/')
        }
        locale={ui.locale}
        direction={ui.direction}
        t={ui.t}
      />
    );
  if (resource.kind !== 'course') notFound();
  return <PublicCourse resource={resource} ui={ui} />;
}
