'use client';

import { createContext, useContext } from 'react';
import type { ReactNode } from 'react';

type ErrorLabels = {
  readonly locale: string;
  readonly direction: 'ltr' | 'rtl';
  readonly title: string;
  readonly description: string;
  readonly retry: string;
  readonly recovery: string;
  readonly recoveryPath: string;
};
const ErrorLabelsContext = createContext<ErrorLabels | null>(null);

/** Only bounded translated labels enter the interactive boundary, never catalogues or DTOs. */
export function ErrorLabelsProvider({
  labels,
  children,
}: {
  readonly labels: ErrorLabels;
  readonly children: ReactNode;
}) {
  return <ErrorLabelsContext.Provider value={labels}>{children}</ErrorLabelsContext.Provider>;
}

export function useErrorLabels() {
  return useContext(ErrorLabelsContext);
}
