import { AccountPage, accountPageMetadata } from '@/components/auth/shell';

export const generateMetadata = () => accountPageMetadata('invitations');
export default function InvitationsPage() {
  return <AccountPage kind="invitations" />;
}
