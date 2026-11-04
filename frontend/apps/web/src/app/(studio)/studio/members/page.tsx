import { AccountPage, accountPageMetadata } from '@/components/auth/shell';

export const generateMetadata = () => accountPageMetadata('members');
export default function MembersPage() {
  return <AccountPage kind="members" />;
}
