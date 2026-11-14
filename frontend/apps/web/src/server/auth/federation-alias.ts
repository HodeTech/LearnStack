/** ADR-0099: the derived federation alias shape (`lsf-` and 32 lowercase hex); the BFF never takes one from the browser. */
export const FEDERATION_ALIAS = /^lsf-[0-9a-f]{32}$/;
/** ADR-0099 Amendment 6: the optional client scope only the federation_test authorization requests. */
export const FEDERATION_TEST_SCOPE = 'federation.test_record';
