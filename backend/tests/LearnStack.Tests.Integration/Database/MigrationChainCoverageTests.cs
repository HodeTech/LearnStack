using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// That <see cref="MigrationChains"/> knows about every chain the repository
/// deploys.
/// </summary>
/// <remarks>
/// <para>
/// <b>The leg the schema sweep cannot supply.</b>
/// <c>Every_Migration_Chain_Has_A_History_Table</c> compares what the database
/// carries against <see cref="MigrationChains.HistoryTables"/> — both sides of one
/// list. A fourth chain that ships without being added there produces three
/// history tables and a three-entry list, and the comparison stays green while the
/// fixture applies a schema smaller than the one that deploys.
/// </para>
/// <para>
/// That is not hypothetical: it happened twice inside Packet 8, in two different
/// fixtures, and cost eight catalogue assertions that silently stopped covering
/// the tables the packet added. Extracting one applier fixed the instance; this
/// fixes the class, by binding the list to the directories on disk.
/// </para>
/// <para>
/// <b>No container, and the Docker trait anyway.</b> It compares a directory scan
/// to a constant and would run in either CI job — but
/// <c>Every_Database_Test_Carries_The_Docker_Trait</c> is keyed on this folder, and
/// a rule with an exception list is weaker than one file is inconvenient. It lives
/// here because it is about <see cref="MigrationChains"/>, which is internal to
/// this assembly.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class MigrationChainCoverageTests
{
    [Fact]
    public void Every_Migration_Chain_On_Disk_Is_Applied_By_The_Fixtures()
    {
        var chains = Directory
            .EnumerateDirectories(BackendSource(), "Migrations", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        chains.Should().NotBeEmpty("a scan finding nothing would satisfy this vacuously");

        chains.Should().HaveSameCount(
            MigrationChains.HistoryTables,
            "every migration chain in backend/src is applied by MigrationChains.ApplyAllAsync, "
            + "and a chain missing from it gives every fixture a smaller schema than the one "
            + $"that deploys. On disk: {string.Join(", ", chains.Select(Path.GetDirectoryName))}");
    }

    private static string BackendSource()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "backend", "src");

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate backend/src from '{AppContext.BaseDirectory}'.");
    }
}
