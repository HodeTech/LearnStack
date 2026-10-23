/** Type-only browser contract; no provider or application identity. */
export type SessionProjection = Readonly<{
  state: 'authenticated';
  displayName: string;
  expiresAt: number;
  limitedByAbsolute: boolean;
  csrfToken: string;
}>;
