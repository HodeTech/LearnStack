import { AccountPage, accountPageMetadata } from '@/components/auth/shell';

export const generateMetadata = () => accountPageMetadata('invitation_policy');
export default function InvitationPolicyPage() {
  return <AccountPage kind="invitation_policy" />;
}
