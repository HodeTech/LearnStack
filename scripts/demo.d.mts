export function isApiListenerEvent(line: string, origin: string): boolean;
export function validateCertificate(
  certificate: Buffer,
  privateKey: Buffer,
  hosts: string[],
  now?: number,
): import('node:crypto').X509Certificate;
