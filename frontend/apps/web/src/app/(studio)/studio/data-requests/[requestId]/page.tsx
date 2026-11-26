import { AccountPage, accountPageMetadata } from '@/components/auth/shell';

export const generateMetadata = () => accountPageMetadata('data_request');
export default function DataRequestPage() {
  return <AccountPage kind="data_request" />;
}
