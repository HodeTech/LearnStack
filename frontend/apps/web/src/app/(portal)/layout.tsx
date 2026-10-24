import type { ReactNode } from 'react';

type PortalLayoutProps = {
  readonly children: ReactNode;
};

export default function PortalLayout({ children }: PortalLayoutProps) {
  return (
    <main id="main-content" tabIndex={-1} className="min-h-screen">
      {children}
    </main>
  );
}
