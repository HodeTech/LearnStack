import { AccountPage, accountPageMetadata } from '@/components/auth/shell';

export const generateMetadata = () => accountPageMetadata('role');
export default function RolePage() {
  return <AccountPage kind="role" />;
}
