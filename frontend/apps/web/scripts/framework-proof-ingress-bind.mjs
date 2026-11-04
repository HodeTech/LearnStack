// Copied ingress child only: observe actual Node listening events before Next imports.
import { Server } from 'node:net';

export function installCopiedIngressBinding(proof) {
  if (!proof || typeof proof.emit !== 'function') throw new Error('Ingress proof binding absent');
  const listen = Server.prototype.listen;
  Server.prototype.listen = function (...args) {
    this.once('listening', () => {
      const address = this.address();
      if (address && typeof address === 'object')
        proof.emit({
          kind: 'bound',
          pid: process.pid,
          port: address.port,
          address: address.address,
        });
    });
    return Reflect.apply(listen, this, args);
  };
}
