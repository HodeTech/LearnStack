import { PublicState } from '@/components/public/state';
import { getPublicUi } from '@/server/public-ui';

export default async function PublicLoading() {
  const ui = await getPublicUi();
  return (
    <PublicState
      state="loading"
      recoveryPath="/"
      locale={ui.locale}
      direction={ui.direction}
      t={ui.t}
    />
  );
}
