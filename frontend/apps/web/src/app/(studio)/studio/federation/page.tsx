import { AccountPage, accountPageMetadata } from '@/components/auth/shell';

export const generateMetadata = () => accountPageMetadata('federation');
export default function FederationPage() {
  return <AccountPage kind="federation" />;
}
