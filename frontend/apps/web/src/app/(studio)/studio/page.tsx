import { AccountShell, accountShellMetadata } from '@/components/auth/shell';

export const generateMetadata = () => accountShellMetadata('studio');
export default function StudioHomePage() {
  return <AccountShell kind="studio" />;
}
