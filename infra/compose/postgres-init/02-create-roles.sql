-- Postgres init script — runs ONCE on a fresh `postgres-data` volume, after
-- 01-create-keycloak-db.sql. Provisions the four-role model of
-- ADR-0003 Amendment 3, whose canonical definition is
-- docs/standards/05-database.md § Database roles.
--
-- WHY FOUR ROLES AND NOT ONE. The single `POSTGRES_USER` this stack used to run
-- everything as owns every table it creates, and an owner bypasses its own
-- policies unless FORCE ROW LEVEL SECURITY is set — and even under FORCE, a
-- runtime that IS the owner defeats the separation the policies exist to make.
-- Every isolation test would then pass against policies that constrain nothing.
--
-- PASSWORDS COME FROM THE ENVIRONMENT. `\getenv` reads each one from the
-- container env, which infra/compose/dev.yml supplies as `${VAR:-…}` with a
-- matching row in `.env.example`, per Standards 12 § Local Infrastructure —
-- compose files carry no bare credential literals. If a variable is unset the
-- placeholder stays unbound and this script fails, which aborts initdb and stops
-- the container: a loud failure rather than four passwordless roles.

\getenv migration_pw LEARNSTACK_MIGRATION_PW
\getenv app_pw       LEARNSTACK_APP_PW
\getenv platform_pw  LEARNSTACK_PLATFORM_PW
\getenv outbox_pw    LEARNSTACK_OUTBOX_PW
\getenv db           POSTGRES_DB

-- Idempotent in the shape 01-create-keycloak-db.sql already uses: PostgreSQL has
-- no CREATE ROLE IF NOT EXISTS, so the statement is generated only when the role
-- is absent and executed with \gexec. `format(%L)` quotes the password for SQL;
-- psql has already substituted `:'x'` into a literal, so the value is never
-- concatenated into the statement text unescaped.
SELECT format('CREATE ROLE learnstack_migration LOGIN PASSWORD %L NOBYPASSRLS', :'migration_pw')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'learnstack_migration')
\gexec

SELECT format('CREATE ROLE learnstack_app LOGIN PASSWORD %L NOBYPASSRLS', :'app_pw')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'learnstack_app')
\gexec

-- BYPASSRLS on these two is bounded by GRANTs, not by policies: the attribute
-- bypasses policies and nothing else, so a role holding it with no table
-- privilege gets `permission denied for table`. The GRANT matrix in
-- Standards 05 is the whole of that bound, and every grant is written in the
-- migration that creates its table — there is deliberately no
-- ALTER DEFAULT PRIVILEGES, so a new table nobody granted fails loudly instead
-- of silently widening a bypass role.
SELECT format('CREATE ROLE learnstack_platform LOGIN PASSWORD %L BYPASSRLS', :'platform_pw')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'learnstack_platform')
\gexec

SELECT format('CREATE ROLE learnstack_outbox_admin LOGIN PASSWORD %L BYPASSRLS', :'outbox_pw')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'learnstack_outbox_admin')
\gexec

-- No `GRANT learnstack_platform TO learnstack_app`, ever. Membership would make
-- BYPASSRLS a standing capability of the application role, reachable from any
-- code path that can execute `SET ROLE` — and a plain SET ROLE survives COMMIT on
-- a PgBouncer transaction-pooled connection, into the next tenant's request.
-- EnterPlatformAdminScope reaches the platform role by a second, separately
-- credentialed connection instead (Standards 05 § How EnterPlatformAdminScope
-- reaches learnstack_platform).

-- :"db" quotes as an identifier. The database name is POSTGRES_DB, which .env
-- may override, so the literal `learnstack` would grant CONNECT on a database
-- that need not exist. Note this grants nothing on the `keycloak` database that
-- 01 creates — Keycloak keeps its own role, and these four have no business
-- there.
GRANT CONNECT ON DATABASE :"db"
    TO learnstack_migration, learnstack_app, learnstack_platform, learnstack_outbox_admin;

-- Since PostgreSQL 15 the public schema no longer grants CREATE to PUBLIC, and
-- the schema is owned by pg_database_owner. Without the CREATE grant below the
-- first migration fails with "permission denied for schema public" — and the
-- tempting fix, making the migration role a superuser or the database owner,
-- reinstates exactly the ownership arrangement FORCE ROW LEVEL SECURITY exists
-- to defeat.
REVOKE ALL   ON SCHEMA public FROM PUBLIC;
GRANT USAGE, CREATE ON SCHEMA public TO learnstack_migration;
GRANT USAGE         ON SCHEMA public
    TO learnstack_app, learnstack_platform, learnstack_outbox_admin;

-- No per-table grants here. This script runs at initdb time, before any table
-- exists, and under the entrypoint's ON_ERROR_STOP a single
-- `relation "…" does not exist` aborts the whole init and the container never
-- becomes healthy. Table grants live in the migration that creates each table.
