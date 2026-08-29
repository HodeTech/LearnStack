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
#
# ── Why this tokenizes instead of splitting on ";" ────────────────────────────
# Npgsql accepts a semicolon INSIDE a quoted value — measured against Npgsql 10:
#
#   Host=h;Password=";secret";Database=d   ->  Password = ';secret'
#
# Splitting on `;` cut that value in half. The first half matched the keyword
# table and was redacted; the second half — `secret"` — matched nothing and was
# printed verbatim, so `make migrate` reported a redacted string that still
# carried the password. Redacting by keyword only works if the keywords are found
# on real field boundaries, so the boundaries are found first.

function normalize_key(text,    key) {
    key = text
    sub(/=.*/, "", key)
    gsub(/[ \t\r\n]/, "", key)

    return tolower(key)
}

# The value with its surrounding quotes removed and doubled inner quotes
# collapsed — Npgsql's own escaping rule.
function unquote(text,    value, quote, inner) {
    value = text
    sub(/^[^=]*=/, "", value)
    gsub(/^[ \t\r\n]+|[ \t\r\n]+$/, "", value)

    quote = substr(value, 1, 1)

    if ((quote == "\"" || quote == "'") &&
        length(value) > 1 &&
        substr(value, length(value), 1) == quote) {
        inner = substr(value, 2, length(value) - 2)
        gsub(quote quote, quote, inner)

        return inner
    }

    return value
}

# Splits `text` into out[1..n] on semicolons that are OUTSIDE quotes, and returns
# n. A doubled quote inside a quoted run is an escaped quote, not its end.
function split_fields(text, out,    i, c, quote, current, count, length_of) {
    count = 0
    current = ""
    quote = ""
    length_of = length(text)

    for (i = 1; i <= length_of; i++) {
        c = substr(text, i, 1)

        if (quote != "") {
            current = current c

            if (c == quote) {
                if (substr(text, i + 1, 1) == quote) {
                    current = current quote
                    i++
                } else {
                    quote = ""
                }
            }
        } else if (c == "\"" || c == "'") {
            quote = c
            current = current c
        } else if (c == ";") {
            out[++count] = current
            current = ""
        } else {
            current = current c
        }
    }

    out[++count] = current

    return count
}

BEGIN {
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
}

{
    input = input (NR > 1 ? "\n" : "") $0
}

END {
    count = split_fields(input, fields)

    if (field == "user") {
        for (i = 1; i <= count; i++) {
            if (normalize_key(fields[i]) in user) {
                print unquote(fields[i]) "\n"
                exit
            }
        }

        exit
    }

    for (i = 1; i <= count; i++) {
        if (i > 1) {
            print ";"
        }

        if (normalize_key(fields[i]) in secret) {
            # The whole field, quotes included: a partial replacement is how the
            # split-on-";" version leaked.
            token = fields[i]
            sub(/=.*/, "=***", token)
            print token
            continue
        }

        # No keyword to key on in a URI-style DSN — `postgres://role:secret@host/db`
        # keeps its password in the userinfo. Npgsql rejects that form outright, so
        # the value is on its way to an error message either way; the userinfo goes
        # whole rather than by halves, because a value that did not parse gives
        # nothing to be confident about.
        token = fields[i]
        gsub(/:\/\/[^@]*@/, "://***@", token)
        print token
    }
}
