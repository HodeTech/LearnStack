import { AccountPage, accountPageMetadata } from '@/components/auth/shell';

export const generateMetadata = () => accountPageMetadata('custom_fields');
export default function CustomFieldPage() {
  return <AccountPage kind="custom_fields" />;
}
