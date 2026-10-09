import type { ReactNode } from 'react';

// ADR-0053: no public representation may survive into another host/request.
export const dynamic = 'force-dynamic';
export const revalidate = 0;
export const fetchCache = 'force-no-store';

type PublicLayoutProps = {
  readonly children: ReactNode;
};

export default function PublicLayout({ children }: PublicLayoutProps) {
  return <main className="min-h-screen">{children}</main>;
}
