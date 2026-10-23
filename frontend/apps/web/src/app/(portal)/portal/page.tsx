import { AccountShell } from '@/components/auth/shell';

export const metadata = { robots: { index: false, follow: false } };
export default function PortalHomePage() {
  return <AccountShell kind="portal" />;
}
