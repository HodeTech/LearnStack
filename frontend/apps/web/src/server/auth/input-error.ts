/** Shared input classification without native session or transport ownership. */
export class BffInputError extends Error {
  constructor() {
    super('BFF input refused');
    this.name = 'BffInputError';
  }
}
