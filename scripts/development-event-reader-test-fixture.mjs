// Test-only provider state for the machine reader's REST boundary; production never imports this file.
import { provider } from './development-realm-test-fixture.mjs';
import { eventReaderDefaultRoleDetachment } from './development-event-reader.mjs';
const copy = value => structuredClone(value);
export function providerWithEventReader() {
  const fixture = provider();
  const { state, reads, writes } = fixture;
  const original = fixture.admin;
  const management = { id: 'owned-management-client', clientId: 'realm-management' };
  const role = { id: 'owned-view-events-role', containerId: management.id, clientRole: true, composite: false, name: 'view-events' };
  let reader;
  let user;
  let scopes = [];
  let scopeMappings = {};
  const mapping = values => ({ clientMappings: { 'realm-management': { id: management.id, client: 'realm-management', mappings: copy(values) } } });
  const get = async path => {
    let result;
    if (path === 'clients?clientId=realm-management&max=2') result = [management];
    else if (path === `clients/${management.id}/roles/view-events`) result = role;
    else if (path === 'clients?clientId=learnstack-event-reader&max=2') result = reader ? [reader] : [];
    else if (reader && path.startsWith(`clients/${reader.id}`)) {
      const suffix = path.slice(`clients/${reader.id}`.length);
      result = suffix === '' ? { ...reader, defaultClientScopes: scopes.map(scope => scope.name) }
        : suffix === '/client-secret' ? { type: 'secret', value: reader.secret }
        : suffix === '/protocol-mappers/models' ? reader.protocolMappers
        : suffix === '/default-client-scopes' ? scopes
        : suffix === '/optional-client-scopes' ? []
        : suffix === '/scope-mappings' ? scopeMappings
        : suffix === '/service-account-user' ? user : undefined;
    } else if (user && path.startsWith(`users/${user.id}/`)) {
      const suffix = path.slice(`users/${user.id}`.length);
      result = suffix === '/role-mappings' ? user.roles
        : suffix === `/role-mappings/clients/${management.id}/composite` ? user.roles.clientMappings?.['realm-management']?.mappings ?? []
        : suffix === '/groups?first=0&max=1' ? [] : undefined;
    }
    if (result === undefined) return original.get(path);
    reads.push(path); return copy(result);
  };
  const post = async (path, body) => {
    if (path === 'clients') {
      writes.push({ method: 'POST', path, body: copy(body) });
      reader = { ...copy(body), id: 'owned-event-reader' };
      reader.protocolMappers = reader.protocolMappers.map(mapper => ({ ...mapper, id: 'owned-reader-mapper' }));
      scopes = [{ id: 'owned-service-account-scope', name: 'service_account' }];
      user = { id: 'owned-event-reader-user', username: 'service-account-learnstack-event-reader',
        serviceAccountClientId: 'learnstack-event-reader', enabled: true, requiredActions: [], credentials: [], attributes: {}, roles: {} };
      state.users.push(user); state.clients.push(reader);
      return { status: 201 };
    }
    if (reader && path === `clients/${reader.id}/scope-mappings/clients/${management.id}`) {
      writes.push({ method: 'POST', path, body: copy(body) }); scopeMappings = mapping(body); return { status: 204 };
    }
    if (user && path === `users/${user.id}/role-mappings/clients/${management.id}`) {
      writes.push({ method: 'POST', path, body: copy(body) }); user.roles = mapping(body); return { status: 204 };
    }
    return original.post(path, body);
  };
  const remove = async (path, body) => {
    if (reader && path === `clients/${reader.id}/default-client-scopes/owned-service-account-scope`) {
      writes.push({ method: 'DELETE', path }); scopes = []; return 204;
    }
    return original.delete(path, body);
  };
  fixture.admin = { ...original, get, post, delete: remove,
    async detachNewEventReaderDefault(binding) { const value = eventReaderDefaultRoleDetachment(binding); await remove(value.path, value.body); } };
  fixture.readerPolicy = () => ({ reader, user, role, scopeMappings });
  return fixture;
}
