import { AccountPage, accountPageMetadata } from '@/components/auth/shell';

export const generateMetadata = () => accountPageMetadata('data_request_erasure');
export default function DataRequestErasurePage() {
  return <AccountPage kind="data_request_erasure" />;
}
