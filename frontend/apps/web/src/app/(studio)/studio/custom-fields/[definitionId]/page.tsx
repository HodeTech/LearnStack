import { AccountPage, accountPageMetadata } from '@/components/auth/shell';

export const generateMetadata = () => accountPageMetadata('custom_field');
export default function CustomFieldPage() {
  return <AccountPage kind="custom_field" />;
}
