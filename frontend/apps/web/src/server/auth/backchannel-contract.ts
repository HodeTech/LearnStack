/** Verified machine credential, retained only by the native session owner. Times are UTC milliseconds. */
export type VerifiedLogoutToken = Readonly<{
  issuer: string;
  sid: string;
  subject: string | null;
  jti: string;
  fingerprint: string;
  issuedAt: number;
  expiresAt: number;
}>;
