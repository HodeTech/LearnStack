import type { ReactNode } from 'react';

type StudioLayoutProps = {
  readonly children: ReactNode;
};

export default function StudioLayout({ children }: StudioLayoutProps) {
  return <main className="min-h-screen bg-slate-50">{children}</main>;
}
