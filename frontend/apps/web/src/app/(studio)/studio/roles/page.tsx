import { AccountPage, accountPageMetadata } from '@/components/auth/shell';

export const generateMetadata = () => accountPageMetadata('roles');
export default function RolesPage() {
  return <AccountPage kind="roles" />;
}
