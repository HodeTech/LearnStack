using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace LearnStack.Tests.Architecture;

internal static class AsyncStoreInventoryDocument
{
    internal static readonly string[] Sections = ["first-party", "vendor", "exclusions", "module-exclusions", "custody"];
    private static readonly string[] CustodyNames =
    [
        "Existing `audit_log`", "Development Keycloak user events, `EVENT_ENTITY`",
        "External diagnostics and local error files", "Database, WAL and backups", "Native BFF session custody",
    ];
    private static readonly Regex TableIdentifier = new(@"^`[a-z_][a-z_0-9]*`$", RegexOptions.CultureInvariant);
    private static readonly Regex Separator = new(@"^:?-{3,}:?$", RegexOptions.CultureInvariant);

    internal static string[] Check(string document, IReadOnlyList<MigrationOperation> platform,
        IReadOnlyList<MigrationOperation> audit, IEnumerable<string> vendorContract,
        IReadOnlyDictionary<string, MigrationOperation[]> modules)
    {
        try
        {
            var failures = new List<string>();
            var firstParty = Read(document, "first-party", "Store", "Copies and subject linkage", "Existing constraint");
            var vendor = Read(document, "vendor", "Vendor table", "Copy or control classification");
            var exclusions = Read(document, "exclusions", "Excluded table", "Scope boundary");
            var moduleExclusions = Read(document, "module-exclusions", "Excluded table", "Scope boundary");
            var custody = Read(document, "custody", "Store or custody boundary", "Subject linkage and limit");
            var firstNames = Names(firstParty);
            var vendorNames = Names(vendor);
            var excludedNames = Names(exclusions);
            Count(firstParty, 10, "first-party");
            Count(vendor, 12, "vendor");
            Compare(excludedNames, ["idempotency_keys"], "explicit scope exclusion");
            Compare(custody.Select(row => row[0]), CustodyNames, "audit/provider/external/volatile custody");
            Compare(vendorNames, vendorContract, "vendor contract classifications");

            var allInventoryNames = firstNames.Concat(vendorNames).Concat(excludedNames)
                .Concat(Names(moduleExclusions)).Append("audit_log").ToArray();
            if (allInventoryNames.Distinct(StringComparer.Ordinal).Count() != allInventoryNames.Length)
                failures.Add("A table appears in more than one inventory class.");
            // No actual-table prefix or inventory membership filter: a newly introduced table
            // must appear in the reviewed inventory or its explicit, bounded exclusion.
            var discovered = AsyncStoreInventoryCensus.Tables(platform)
                .Concat(AsyncStoreInventoryCensus.Tables(audit))
                .Concat(modules.Values.SelectMany(AsyncStoreInventoryCensus.Tables)).ToArray();
            if (discovered.Distinct(StringComparer.Ordinal).Count() != discovered.Length)
                failures.Add("A table is declared by more than one migration chain.");
            Compare(discovered, allInventoryNames, "complete migration census");
            return failures.ToArray();

            void Count(IReadOnlyCollection<string[]> rows, int expected, string label)
            {
                if (rows.Count != expected) failures.Add($"Expected {expected} {label} rows; found {rows.Count}.");
            }
            void Compare(IEnumerable<string> actual, IEnumerable<string> expected, string label)
            {
                var actualSet = actual.ToHashSet(StringComparer.Ordinal);
                var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
                if (!actualSet.SetEquals(expectedSet))
                    failures.Add($"{label}: unexpected [{string.Join(", ", actualSet.Except(expectedSet).Order(StringComparer.Ordinal))}]; "
                        + $"missing [{string.Join(", ", expectedSet.Except(actualSet).Order(StringComparer.Ordinal))}].");
            }
        }
        catch (FormatException exception)
        {
            return [exception.Message];
        }
    }

    internal static string[][] Read(string document, string section, params string[] header)
    {
        var start = $"<!-- async-store-{section}:start -->";
        var end = $"<!-- async-store-{section}:end -->";
        var startIndex = document.IndexOf(start, StringComparison.Ordinal);
        var endIndex = document.IndexOf(end, StringComparison.Ordinal);
        if (startIndex < 0 || endIndex < startIndex
            || document.IndexOf(start, startIndex + start.Length, StringComparison.Ordinal) >= 0
            || document.IndexOf(end, endIndex + end.Length, StringComparison.Ordinal) >= 0)
            throw new FormatException($"Missing, repeated or reversed {section} census markers.");
        var lines = document[(startIndex + start.Length)..endIndex].Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => line.Length != 0).ToArray();
        if (lines.Length < 2) throw new FormatException($"Missing {section} table.");
        var cells = lines.Select(line =>
        {
            if (!line.StartsWith('|') || !line.EndsWith('|'))
                throw new FormatException($"Malformed {section} table row.");
            var columns = line[1..^1].Split('|', StringSplitOptions.TrimEntries);
            if (columns.Length != header.Length || columns.Any(string.IsNullOrWhiteSpace))
                throw new FormatException($"Missing {section} classification or malformed row.");
            return columns;
        }).ToArray();
        if (!cells[0].SequenceEqual(header, StringComparer.Ordinal)
            || cells[1].Any(column => !Separator.IsMatch(column)))
            throw new FormatException($"Malformed {section} table header.");
        var rows = cells.Skip(2).ToArray();
        if (rows.Select(row => row[0]).Distinct(StringComparer.Ordinal).Count() != rows.Length)
            throw new FormatException($"Duplicate {section} inventory row.");
        return rows;
    }

    private static string[] Names(IEnumerable<string[]> rows) => rows.Select(row =>
    {
        if (!TableIdentifier.IsMatch(row[0])) throw new FormatException("Malformed inventory table identifier.");
        return row[0][1..^1];
    }).ToArray();
}
