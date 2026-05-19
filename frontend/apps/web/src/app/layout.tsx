import type { Metadata } from 'next';
import type { ReactNode } from 'react';

import './globals.css';

export const metadata: Metadata = {
  title: 'LearnStack',
  description: 'Multi-tenant core platform for building education products.',
};

type RootLayoutProps = {
  readonly children: ReactNode;
};

export default function RootLayout({ children }: RootLayoutProps) {
  return (
    <html lang="en">
      <body className="bg-ls-bg text-ls-fg font-sans antialiased">{children}</body>
    </html>
  );
}
