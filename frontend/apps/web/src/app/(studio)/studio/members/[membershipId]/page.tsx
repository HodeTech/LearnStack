import { AccountPage, accountPageMetadata } from '@/components/auth/shell';

export const generateMetadata = () => accountPageMetadata('member');
export default function MemberPage() {
  return <AccountPage kind="member" />;
}
