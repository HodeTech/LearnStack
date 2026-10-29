import { AccountPage, accountPageMetadata } from '@/components/auth/shell';

export const generateMetadata = () => accountPageMetadata('profile');
export default function OwnProfilePage() {
  return <AccountPage kind="profile" />;
}
