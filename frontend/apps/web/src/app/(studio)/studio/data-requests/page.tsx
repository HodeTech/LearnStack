import { AccountPage, accountPageMetadata } from '@/components/auth/shell';

export const generateMetadata = () => accountPageMetadata('data_requests');
export default function DataRequestsPage() {
  return <AccountPage kind="data_requests" />;
}
