-- Postgres init script — runs ONCE on a fresh `postgres-data` volume.
-- Creates the `keycloak` database the Keycloak service uses to store its
-- realm + user state. Keycloak shares the `learnstack` Postgres role + the
-- same Postgres instance (dev-only convenience; production isolates Keycloak
-- in its own Postgres cluster per Standards 12 § Database Operations).
--
-- If `postgres-data` already exists, this script does NOT re-run; either
-- `docker compose -f infra/compose/dev.yml down -v` to wipe + reseed, or
-- manually `CREATE DATABASE keycloak OWNER learnstack;` once.

SELECT 'CREATE DATABASE keycloak OWNER learnstack'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'keycloak')
\gexec
