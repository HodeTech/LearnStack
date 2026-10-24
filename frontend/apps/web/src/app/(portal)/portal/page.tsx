import { AccountShell, accountShellMetadata } from '@/components/auth/shell';

export const generateMetadata = () => accountShellMetadata('portal');
export default function PortalHomePage() {
  return <AccountShell kind="portal" />;
}
