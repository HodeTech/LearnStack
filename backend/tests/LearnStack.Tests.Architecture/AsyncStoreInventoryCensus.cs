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

    internal static MigrationOperation[] Operations(DbContext context)
    {
        var assembly = context.GetService<IMigrationsAssembly>();
        return assembly.Migrations.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(pair => assembly.CreateMigration(pair.Value, context.Database.ProviderName!).UpOperations)
            .ToArray();
    }

    internal static HashSet<string> Tables(IEnumerable<MigrationOperation> operations)
    {
        var tables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case CreateTableOperation create:
                    Create(Name(create.Schema, create.Name), false);
                    break;
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
            var tokens = SqlTokens.Matches(sql.TrimStart('\uFEFF')).Where(match => !match.Groups["comment"].Success)
                .Select(match => new SqlToken(match.Value, match.Groups["word"].Success,
                    match.Groups["quoted"].Success, match.Groups["literal"].Success)).ToArray();
            for (var index = 0; index < tokens.Length; index++)
            {
                // Dynamic table DDL cannot be resolved by this reader. Refuse it rather than
                // treating a literal embedded in EXECUTE/format as a harmless absence of tables.
                if (tokens[index].Literal && DynamicTableDeclaration.IsMatch(tokens[index].Text))
                    throw new FormatException("Dynamic table DDL requires an explicit census reader decision.");

                // Function-level SET clauses do not change migration name resolution.
                if ((index == 0 || At(index - 1).Text == ";")
                    && Is(index, "SET") && Is(index + 1, "search_path"))
                {
                    var path = At(index + 3).Text.Trim('\'');
                    if (path != "public")
                        throw new FormatException("The table census does not assume a non-public search path.");
                }

                if (Is(index, "CREATE"))
                {
                    var cursor = index + 1;
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

            SqlToken At(int index) => index < tokens.Length ? tokens[index] : default;
            bool Is(int index, string word) => At(index).Word
                && string.Equals(At(index).Text, word, StringComparison.OrdinalIgnoreCase);
            void Require(int index, string word)
            {
                if (!Is(index, word)) throw new FormatException("Malformed table declaration.");
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

    private static string Name(string? schema, string table) =>
        schema is null or "public" ? table : schema + "." + table;

    private readonly record struct SqlToken(string Text, bool Word, bool Quoted, bool Literal);
}
