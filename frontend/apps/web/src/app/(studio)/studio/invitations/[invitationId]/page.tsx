import { AccountPage, accountPageMetadata } from '@/components/auth/shell';

export const generateMetadata = () => accountPageMetadata('invitation');
export default function InvitationPage() {
  return <AccountPage kind="invitation" />;
}
