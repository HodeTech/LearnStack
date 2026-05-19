import type { ReactNode } from 'react';

type PortalLayoutProps = {
  readonly children: ReactNode;
};

export default function PortalLayout({ children }: PortalLayoutProps) {
  return <main className="min-h-screen bg-white">{children}</main>;
}
