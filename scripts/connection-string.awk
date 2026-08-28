# Reads an Npgsql connection string on stdin; writes one field on stdout.
#
#   awk -f scripts/connection-string.awk -v field=user      < value   # the role
#   awk -f scripts/connection-string.awk -v field=redacted  < value   # safe to echo
#
# Why a script and not two inline expressions in the `migrate` recipe: both
# passes need the SAME keyword table, and the version that had two of them had
# two different ones. It recognised `Username=` and `Password=` only, so a
# perfectly valid `.env` written as `UID=learnstack_app;PWD=...` — Npgsql accepts
# both, measured against Npgsql 10 — read the role as empty AND printed the
# password unredacted, in the one target whose whole purpose is keeping that
# credential in one place.
#
# The runtime half of this check lives in C#, in
# `PersistenceCompositionExtensions.BuildApplicationDataSource`, where a real
# `NpgsqlConnectionStringBuilder` does the parsing and no keyword table is
# needed. `make migrate` cannot reach it: `dotnet ef` constructs its context
# through the design-time factory, which never runs the composition root. So the
# table is duplicated here on purpose, and
# `Migrate_Target_Refuses_An_Aliased_Runtime_Credential` executes this recipe
# against the aliases to keep the two halves honest.

BEGIN {
    RS = ";"
    ORS = ""

    # Every alias Npgsql 10 parses into Username, and into Password, with the
    # spaces stripped and lowercased: `User Name`, `USERID` and `Pwd` are all
    # here. Measured by round-tripping each through NpgsqlConnectionStringBuilder,
    # not read off a documentation page.
    user["username"] = 1
    user["userid"] = 1
    user["uid"] = 1

    secret["password"] = 1
    secret["psw"] = 1
    secret["pwd"] = 1

    found = 0
}

{
    key = $0
    sub(/=.*/, "", key)
    gsub(/[ \t\r\n]/, "", key)
    key = tolower(key)

    if (field == "user") {
        if (key in user && !found) {
            value = $0
            sub(/^[^=]*=/, "", value)
            gsub(/^[ \t\r\n]+|[ \t\r\n]+$/, "", value)
            print value
            found = 1
        }
        next
    }

    if (NR > 1) {
        print ";"
    }

    if (key in secret) {
        token = $0
        sub(/=.*/, "=***", token)
        print token
        next
    }

    # No keyword to key on in a URI-style DSN — `postgres://role:secret@host/db`
    # keeps its password in the userinfo. Npgsql rejects that form outright, so
    # the value is on its way to an error message either way; the userinfo goes
    # whole rather than by halves, because a value that did not parse gives
    # nothing to be confident about.
    token = $0
    gsub(/:\/\/[^@]*@/, "://***@", token)
    print token
}

END {
    if (field == "user" && found) {
        print "\n"
    }
}
