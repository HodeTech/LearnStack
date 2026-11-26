// @vitest-environment node
import { describe, expect, it } from 'vitest';

import {
  ADMINISTRATOR_ROUTES,
  PERSON_ROUTES,
  dataRequestSources,
  documentedReasons,
  documentedRouteReasons,
  identityDataRequestReasons,
  lockeyReasons,
  spelledReasons,
} from './data-request-reasons';

/** The catalogue tests trust this scanner to see every reason, so it is tested on what it must and must not see. */
describe('the data-request reason scanner', () => {
  it('sees a reason whatever characters it is spelled with, digits included', () => {
    expect(
      lockeyReasons(
        'Refuse("lockey_blocked_hold"); Refuse("lockey_limit_2"); Refuse("lockey_x2y");',
      ),
    ).toEqual(['lockey_blocked_hold', 'lockey_limit_2', 'lockey_x2y']);
    expect(
      documentedReasons('409 lockey_limit_2, lockey_subject_is_requester and data_export_v2.'),
    ).toEqual(['lockey_limit_2', 'lockey_subject_is_requester', 'lockey_data_export_v2']);
    // A key written in upper case, split by a dash, or not a literal is not a reason.
    expect(lockeyReasons('"LOCKEY_x" "lockey-x" lockey_unquoted "lockey_"')).toEqual([]);
    expect(documentedReasons('lockey_ and lockey_* and data_export')).toEqual([]);
  });

  it('reads every C# file of the Identity data-request code, however deep', () => {
    const files = dataRequestSources();
    expect(files.length).toBeGreaterThan(10);
    for (const name of [
      'DataRequestService.cs',
      'DataExportDownloadService.cs',
      'RetainedDataRequestHandlers.cs',
      'DataRequestHandlers.cs',
    ])
      expect(files).toContain(name);
    expect(files.every((name) => name.endsWith('.cs'))).toBe(true);
  });

  it('reads the person’s routes and the administrator’s routes apart', () => {
    const person = documentedRouteReasons(PERSON_ROUTES);
    const administrator = documentedRouteReasons(ADMINISTRATOR_ROUTES);
    // Only the person's download answers the ticket limit; the administrator never receives the file.
    expect(person.has('lockey_data_export_ticket_limit')).toBe(true);
    expect(administrator.has('lockey_data_export_ticket_limit')).toBe(false);
    for (const reasons of [person, administrator]) {
      expect(reasons.has('lockey_request_already_active')).toBe(true);
      expect(reasons.has('lockey_data_request_not_cancellable')).toBe(true);
    }
    // A prefix that names no route documents nothing, and the code's reasons are in every catalogue's set.
    expect(documentedRouteReasons(['/api/v1/identity/nothing']).size).toBe(0);
    expect(documentedRouteReasons([]).size).toBe(0);
    for (const routes of [PERSON_ROUTES, ADMINISTRATOR_ROUTES]) {
      const all = identityDataRequestReasons(routes);
      for (const reason of [...spelledReasons(), ...documentedRouteReasons(routes)])
        expect(all.has(reason), reason).toBe(true);
      expect(all.size).toBeGreaterThan(15);
    }
    expect(spelledReasons().has('lockey_subject_not_admitted')).toBe(true);
  });
});
