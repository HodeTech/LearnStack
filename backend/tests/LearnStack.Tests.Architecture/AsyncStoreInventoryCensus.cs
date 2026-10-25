using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// Structural table discovery from the operations the migration runner actually consumes.
/// This is a bounded table-declaration reader, not a SQL execution or field-value proof.
/// The independent integration census still checks the applied PostgreSQL catalogue.
/// </summary>
internal static class AsyncStoreInventoryCensus
{
    private static readonly Regex SqlTokens = new(
        "(?<comment>--[^\\r\\n]*|/\\*[\\s\\S]*?\\*/)|(?<literal>'(?:''|[^'])*')|"
        + "(?<quoted>\"(?:\"\"|[^\"])*\")|(?<dollar>\\$(?:[A-Za-z_][A-Za-z_0-9]*)?\\$)|"
        + "(?<word>[A-Za-z_][A-Za-z_0-9$]*)|(?<symbol>\\S)",
        RegexOptions.CultureInvariant);
    private static readonly Regex DynamicTableDeclaration = new(
        @"\b(?:CREATE\s+(?:(?:TEMP|TEMPORARY|UNLOGGED|FOREIGN)\s+)?TABLE|DROP\s+TABLE|ALTER\s+TABLE)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // The pinned installer has precisely these non-table dynamic command families.
    // Resolve literal concatenation only; do not evaluate format(), variables or arbitrary SQL.
    private static readonly Regex PinnedInstallerCommand = new(
        "^(?:CREATE SCHEMA \"public\";|ALTER SEQUENCE \"public\"\\."
        + "(?:counter|hash|jobparameter|jobqueue|list|set|state|job)_id_seq "
        + "(?:AS bigint )?MAXVALUE 9223372036854775807|"
        + "CREATE INDEX ix_hangfire_job_statename_is_not_null ON job USING btree\\(statename\\) "
        + "INCLUDE \\(id\\) WHERE statename IS NOT NULL)$",
        RegexOptions.CultureInvariant);

    internal static MigrationOperation[] Operations(DbContext context)
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        return assembly.Migrations.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(pair =>
            {
                var operations = assembly.CreateMigration(pair.Value, context.Database.ProviderName!).UpOperations;
                foreach (var operation in operations)
                    operation["InventoryOrigin"] = $"{context.GetType().Name}/{pair.Key}";
                return operations;
            }).ToArray();
    }

    internal static HashSet<string> Tables(IEnumerable<MigrationOperation> operations)
    {
        var tables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            try
            {
                switch (operation)
                {
                    case CreateTableOperation create:
                        Create(Name(create.Schema, create.Name), false);
                        break;
                    case DropSchemaOperation:
                        throw new FormatException("Schema mutation requires an explicit census reader decision.");
                    case DropTableOperation drop:
                        Drop(Name(drop.Schema, drop.Name), false);
                        break;
                    case RenameTableOperation rename:
                        var previous = Name(rename.Schema, rename.Name);
                        Drop(previous, false);
                        Create(Name(rename.NewSchema ?? rename.Schema, rename.NewName ?? rename.Name), false);
                        break;
                    case SqlOperation sql:
                        ReadSql(sql.Sql);
                        break;
                }
            }
            catch (FormatException exception)
            {
                throw new FormatException($"{operation["InventoryOrigin"] ?? "Planted migration"} "
                    + $"({operation.GetType().Name}): {exception.Message}", exception);
            }
        }
        return tables;

        void Create(string name, bool conditional)
        {
            if (!tables.Add(name) && !conditional)
                throw new FormatException($"Duplicate table declaration: {name}");
        }

        void Drop(string name, bool conditional)
        {
            if (!tables.Remove(name) && !conditional)
                throw new FormatException($"Dropped table is absent from the census: {name}");
        }

        void ReadSql(string sql)
        {
            var tokens = Tokenize(sql.TrimStart('\uFEFF'));
            string? dollarBody = null;
            var selecting = false;
            for (var index = 0; index < tokens.Length; index++)
            {
                if (tokens[index].Dollar)
                {
                    if (dollarBody is null) dollarBody = tokens[index].Text;
                    else if (dollarBody == tokens[index].Text) dollarBody = null;
                }
                // Outside procedural dollar bodies SELECT INTO creates a table. Inside them
                // PostgreSQL uses it for assignment to PL/pgSQL variables instead.
                if (dollarBody is null)
                {
                    if (At(index).Text == ";") selecting = false;
                    if (Is(index, "SELECT")) selecting = true;
                    if (selecting && Is(index, "INTO"))
                        throw new FormatException("SELECT INTO table creation requires an explicit census reader decision.");
                }
                if (Is(index, "DO") && !Is(index + 1, "NOTHING") && !Is(index + 1, "UPDATE"))
                {
                    var body = index + 1;
                    if (Is(body, "LANGUAGE")) body += 2;
                    if (!At(body).Dollar)
                        throw new FormatException("Non-dollar procedural bodies require an explicit census reader decision.");
                }
                if (IdentifierIs(index, "set_config") && At(index + 1).Text == "("
                    && (!At(index + 2).Literal || At(index + 3).Text != ","
                        || string.Equals(At(index + 2).Text, "'search_path'", StringComparison.OrdinalIgnoreCase)))
                    throw new FormatException("set_config search-path resolution requires an explicit census reader decision.");
                if (((Is(index, "DROP") || Is(index, "ALTER")) && Is(index + 1, "SCHEMA"))
                    || (Is(index, "IMPORT") && Is(index + 1, "FOREIGN") && Is(index + 2, "SCHEMA")))
                    throw new FormatException("Schema mutation requires an explicit census reader decision.");
                if ((Is(index, "CREATE") || Is(index, "ALTER") || Is(index, "DROP"))
                    && Is(index + 1, "MATERIALIZED"))
                    throw new FormatException("Materialized view storage requires an explicit census reader decision.");
                if (Is(index, "EXECUTE") && !IsStaticExecuteClause(index))
                    RequirePinnedExecute(index + 1);

                // Dynamic table DDL cannot be resolved by this reader. Refuse it rather than
                // treating a literal embedded in EXECUTE/format as a harmless absence of tables.
                if (tokens[index].Literal && DynamicTableDeclaration.IsMatch(tokens[index].Text))
                    throw new FormatException("Dynamic table DDL requires an explicit census reader decision.");

                // Function-header SET clauses are distinct from statements, including those
                // in DO bodies. Resolve only the pinned public path; refuse aliases elsewhere.
                if (Is(index, "SET") && (index == 0 || At(index - 1).Text == ";"
                    || Is(index - 1, "BEGIN") || Is(index - 1, "THEN") || Is(index - 1, "ELSE") || Is(index - 1, "LOOP")))
                {
                    var cursor = index + 1;
                    if (Is(cursor, "LOCAL") || Is(cursor, "SESSION")) cursor++;
                    if (IdentifierIs(cursor, "search_path") || IdentifierIs(cursor, "schema"))
                    {
                        var schemaAlias = IdentifierIs(cursor++, "schema");
                        if (!schemaAlias)
                        {
                            if (At(cursor).Text != "=" && !Is(cursor, "TO"))
                                throw new FormatException("Unsupported search-path assignment.");
                            cursor++;
                        }
                        var path = At(cursor++).Text.Trim('\'', '"');
                        if (path != "public" || (cursor < tokens.Length && At(cursor).Text != ";"))
                            throw new FormatException("The table census does not assume a non-public search path.");
                    }
                }

                if (Is(index, "CREATE"))
                {
                    var cursor = index + 1;
                    if (Is(cursor, "OR") && Is(cursor + 1, "REPLACE")) cursor += 2;
                    if (Is(cursor, "FUNCTION") || Is(cursor, "PROCEDURE"))
                    {
                        // A quoted routine body is opaque to this declaration reader,
                        // just like a quoted DO body. Split EXECUTE text can hide table
                        // DDL until a later SELECT/CALL executes the routine.
                        while (cursor < tokens.Length && At(cursor).Text != ";" && !Is(cursor, "AS")) cursor++;
                        if (!Is(cursor, "AS") || !At(cursor + 1).Dollar)
                            throw new FormatException("Non-dollar procedural bodies require an explicit census reader decision.");
                        continue;
                    }
                    if (Is(cursor, "TEMP") || Is(cursor, "TEMPORARY") || Is(cursor, "UNLOGGED")
                        || Is(cursor, "FOREIGN") || Is(cursor, "GLOBAL") || Is(cursor, "LOCAL"))
                        throw new FormatException("Unsupported table persistence declaration.");
                    if (!Is(cursor, "TABLE")) continue;
                    cursor++;
                    var conditional = Is(cursor, "IF");
                    if (conditional)
                    {
                        Require(cursor + 1, "NOT");
                        Require(cursor + 2, "EXISTS");
                        cursor += 3;
                    }
                    Create(TableName(ref cursor), conditional);
                }
                else if (Is(index, "DROP") && Is(index + 1, "TABLE"))
                {
                    var cursor = index + 2;
                    var conditional = Is(cursor, "IF");
                    if (conditional) { Require(cursor + 1, "EXISTS"); cursor += 2; }
                    do
                    {
                        Drop(TableName(ref cursor), conditional);
                    } while (At(cursor++).Text == ",");
                }
                else if (Is(index, "ALTER") && Is(index + 1, "TABLE"))
                {
                    var cursor = index + 2;
                    if (Is(cursor, "IF")) { Require(cursor + 1, "EXISTS"); cursor += 2; }
                    if (Is(cursor, "ONLY")) cursor++;
                    var previous = TableName(ref cursor);
                    if (Is(cursor, "SET") && Is(cursor + 1, "SCHEMA"))
                        throw new FormatException("Table schema moves require an explicit census reader decision.");
                    if (!Is(cursor, "RENAME") || !Is(cursor + 1, "TO")) continue;
                    cursor += 2;
                    var next = TableName(ref cursor);
                    Drop(previous, false);
                    Create(next, false);
                }
            }
            if (dollarBody is not null) throw new FormatException("Unterminated SQL dollar body.");

            SqlToken At(int index) => index >= 0 && index < tokens.Length ? tokens[index] : default;
            bool Is(int index, string word) => At(index).Word
                && string.Equals(At(index).Text, word, StringComparison.OrdinalIgnoreCase);
            bool IdentifierIs(int index, string name) => Is(index, name)
                || (At(index).Quoted && At(index).Text == "\"" + name + "\"");
            bool IsStaticExecuteClause(int index)
            {
                if (dollarBody is not null) return false;
                var start = index;
                while (start > 0 && At(start - 1).Text != ";") start--;
                if (Is(index + 1, "ON")) return Is(start, "GRANT") || Is(start, "REVOKE");
                return (Is(index + 1, "FUNCTION") || Is(index + 1, "PROCEDURE")) && Is(start, "CREATE")
                    && (Is(start + 1, "TRIGGER") || (Is(start + 1, "CONSTRAINT") && Is(start + 2, "TRIGGER")));
            }
            void Require(int index, string word)
            {
                if (!Is(index, word)) throw new FormatException("Malformed table declaration.");
            }
            void RequirePinnedExecute(int cursor)
            {
                var command = new StringBuilder();
                var parentheses = 0;
                var expectingLiteral = true;
                while (cursor < tokens.Length && At(cursor).Text != ";")
                {
                    var token = At(cursor++);
                    if (token.Literal && expectingLiteral)
                    {
                        command.Append(token.Text[1..^1].Replace("''", "'", StringComparison.Ordinal));
                        expectingLiteral = false;
                    }
                    else if (token.Text == "(" && expectingLiteral) parentheses++;
                    else if (token.Text == ")" && !expectingLiteral && parentheses > 0) parentheses--;
                    else if (token.Text == "|" && At(cursor).Text == "|" && !expectingLiteral)
                    {
                        cursor++;
                        expectingLiteral = true;
                    }
                    else throw new FormatException("Unresolved dynamic EXECUTE requires an explicit census reader decision.");
                }
                if (expectingLiteral || parentheses != 0 || !PinnedInstallerCommand.IsMatch(command.ToString()))
                    throw new FormatException("Dynamic EXECUTE is outside the pinned non-table installer commands.");
            }
            string Identifier(ref int cursor)
            {
                var token = At(cursor++);
                if (token.Quoted) return token.Text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
                if (token.Word) return token.Text.ToLowerInvariant();
                throw new FormatException("Malformed table identifier.");
            }
            string TableName(ref int cursor)
            {
                var first = Identifier(ref cursor);
                if (At(cursor).Text != ".") return first;
                cursor++;
                return Name(first, Identifier(ref cursor));
            }
        }
    }

    private static SqlToken[] Tokenize(string sql)
    {
        var tokens = new List<SqlToken>();
        var offset = 0;
        string? delimiter = null;
        var closing = -1;
        while (offset < sql.Length)
        {
            // A body's closing delimiter is lexical, even in END$$. Do not let the
            // ordinary identifier token consume it and hide subsequent SELECT INTO.
            if (delimiter is not null && offset == closing)
            {
                tokens.Add(new SqlToken(delimiter, false, false, false, true));
                offset += delimiter.Length;
                delimiter = null;
                continue;
            }
            var match = SqlTokens.Match(sql, offset, (delimiter is null ? sql.Length : closing) - offset);
            if (!match.Success)
            {
                if (delimiter is null) break;
                offset = closing;
                continue;
            }
            offset = match.Index + match.Length;
            if (match.Groups["comment"].Success) continue;
            tokens.Add(new SqlToken(match.Value, match.Groups["word"].Success,
                match.Groups["quoted"].Success, match.Groups["literal"].Success, match.Groups["dollar"].Success));
            if (delimiter is null && match.Groups["dollar"].Success)
            {
                delimiter = match.Value;
                closing = sql.IndexOf(delimiter, offset, StringComparison.Ordinal);
                if (closing < 0) throw new FormatException("Unterminated SQL dollar body.");
            }
        }
        return tokens.ToArray();
    }

    private static string Name(string? schema, string table) => schema is null or "public" ? table
        : throw new FormatException("Non-public table storage requires an explicit census reader decision.");

    private readonly record struct SqlToken(string Text, bool Word, bool Quoted, bool Literal, bool Dollar);
}
