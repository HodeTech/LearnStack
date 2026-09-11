using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// File-system rules that survive any reflection-based check.
/// These walk the working copy at test time, so no compiled assembly is needed.
/// </summary>
public sealed class RepositoryLayoutTests
{
    /// <summary>Cached `["web"]` so the `BeEquivalentTo` call below does not
    /// allocate a fresh array on every test invocation (CA1861).</summary>
    private static readonly string[] AllowedFrontendApps = ["web"];

    /// <summary>
    /// ADR-0018: domain-specific shapes live as tenant customization data, not code.
    /// A `Verticals/` source folder at any level under `backend/src` is forbidden.
    /// </summary>
    [Fact]
    public void No_Source_Folder_Named_Verticals()
    {
        var srcRoot = RepositoryPaths.BackendSrc();

        var offenders = Directory
            .EnumerateDirectories(srcRoot, "Verticals", SearchOption.AllDirectories)
            .ToArray();

        offenders.Should().BeEmpty(
            "ADR-0018 supersedes ADR-0011; tenant-specific shapes belong to the Customization module's data, " +
            "not to a `Verticals/` source folder. See docs/decisions/0018-tenant-driven-customization-model.md.");
    }

    /// <summary>
    /// ADR-0009: the tenant-facing frontend ships as a single Next.js application
    /// under `frontend/apps/web`. A peer `frontend/apps/studio` or `frontend/apps/portal`
    /// is deferred until the triggers in ADR-0009 fire — adding one without an ADR
    /// is a structural deviation.
    /// </summary>
    [Fact]
    public void Frontend_Has_Only_The_Web_App()
    {
        var appsRoot = RepositoryPaths.FrontendApps();

        Directory.Exists(appsRoot).Should().BeTrue(
            $"`{appsRoot}` must exist — Phase 01 ships the frontend monorepo with `apps/web`. " +
            "If you intentionally removed it, update this test and ADR-0009 together.");

        // Filter dotted directories (.tmp, .cache, .turbo, etc.) — they're tool
        // byproducts, not peer Next apps; ADR-0009 cares about the latter only.
        var appNames = Directory
            .EnumerateDirectories(appsRoot)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Where(name => !name!.StartsWith('.'))
            .ToArray();

        appNames.Should().BeEquivalentTo(
            AllowedFrontendApps,
            "ADR-0009 keeps the tenant-facing frontend as one Next.js app. " +
            "Add a new ADR before splitting (studio / portal extraction is mechanical, " +
            "but the decision must be recorded).");
    }

    /// <summary>
    /// Standards 14 § Commits: the commit-subject rule is one script — the
    /// <c>commit-msg</c> hook — which CI's commit-hygiene step runs, and whose type list
    /// is the standard's table.
    /// </summary>
    [Fact]
    public void Commit_Subject_Grammar_Is_Stated_Once()
    {
        // Three copies of one rule drifted before anything compared them: the standard
        // listed nine types, the hook admitted eleven and CI ten; the scope characters
        // disagreed; and the two scripts judged different text — the hook the file's
        // first line, CI git's joined first paragraph. Subjects passed locally and failed
        // the pull request. CI now runs the hook itself, so there is one rule to state.
        var hookPath = Path.Combine(RepositoryPaths.RepoRoot(), ".githooks", "commit-msg");

        CommitHygieneStepCode().Should()
            .Contain(".githooks/commit-msg", "CI judges a subject by running the hook, not a copy of it")
            .And.Contain("LEARNSTACK_COMMIT_MSG_STRICT=1", "a pull request refuses what a local fixup workflow needs")
            .And.NotContain("grep -qE", "a second grammar in CI is the drift this rule exists to prevent");

        GrammarTypes(File.ReadAllText(hookPath)).Should().BeEquivalentTo(
            StandardTypes(),
            "the standard's table is the list a reader chooses from, so it names what the "
            + "hook admits — no more and no fewer");

        // What the hook does, run for real: two copies that agreed with each other could
        // still both be wrong, and a check that compared text could not see a hook that
        // matched its pattern and then forgot to fail.
        foreach (var (message, local, strict) in Subjects)
        {
            RunCommitMsgHook(hookPath, message, editorRan: false, strict: false)
                .Should().Be(local, "locally, `{0}`", message);
            RunCommitMsgHook(hookPath, message, editorRan: false, strict: true)
                .Should().Be(strict, "in CI, `{0}`", message);
        }

        // With an editor, git strips comment lines before storing the message; without
        // one (`-m`, `-F`) it keeps them, so the same file has two different subjects.
        const string Commented = "#42 note\n\nfeat: add the host map\n";
        RunCommitMsgHook(hookPath, Commented, editorRan: true, strict: false).Should().Be(0);
        RunCommitMsgHook(hookPath, Commented, editorRan: false, strict: false).Should().Be(1);
    }

    /// <summary>A message, and the hook's exit code for it locally and in CI.</summary>
    private static readonly (string Message, int Local, int Strict)[] Subjects =
    [
        ("feat(tenancy): add the host map", 0, 0),
        ("ci: pin the SDK", 0, 0),
        ("style: format", 0, 0),
        ("feat!: drop the v1 route", 0, 0),
        ("fix(kernel, audit)!: close the leak", 0, 0),
        ("docs(api/tenancy): name the header\n\nThe body is free text.\n", 0, 0),
        ("docs: " + new string('§', 66), 0, 0),
        ("docs: " + new string('§', 67), 1, 1),
        ("feat: add the host map and enough words that the second\nline makes it too long\n", 1, 1),
        ("feat:  \n", 1, 1),
        ("Feat: add", 1, 1),
        ("feat(API): add", 1, 1),
        ("feature: add", 1, 1),
        ("feat(): add", 1, 1),
        ("feat:add", 1, 1),
        ("feat(a:b): add", 1, 1),
        ("Update README", 1, 1),
        ("Merge branch 'side'", 1, 1),
        ("Revert \"feat: add\"\n\nThis reverts commit 0000000.\n", 1, 1),
        ("fixup! feat: add", 0, 1),
        ("amend! feat: add", 0, 1),
        ("", 0, 1),
    ];

    /// <summary>
    /// Runs the <c>commit-msg</c> hook on a message and returns its exit code.
    /// </summary>
    /// <remarks>
    /// From an empty directory with the global and system git configuration switched
    /// off, so neither an unfinished merge in the working copy nor a developer's own
    /// <c>commit.cleanup</c> can change the verdict. <c>GIT_EDITOR=:</c> is what git sets
    /// when no editor ran; leaving it unset is the editor case.
    /// </remarks>
    private static int RunCommitMsgHook(string hookPath, string message, bool editorRan, bool strict)
    {
        var directory = Directory.CreateTempSubdirectory("learnstack-commit-msg");

        try
        {
            var file = Path.Combine(directory.FullName, "COMMIT_EDITMSG");
            File.WriteAllText(file, message);

            var startInfo = new System.Diagnostics.ProcessStartInfo("bash")
            {
                WorkingDirectory = directory.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            startInfo.ArgumentList.Add(hookPath);
            startInfo.ArgumentList.Add(file);
            startInfo.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
            startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            startInfo.Environment.Remove("GIT_EDITOR");
            startInfo.Environment.Remove("LEARNSTACK_COMMIT_MSG_STRICT");

            if (!editorRan)
            {
                startInfo.Environment["GIT_EDITOR"] = ":";
            }

            if (strict)
            {
                startInfo.Environment["LEARNSTACK_COMMIT_MSG_STRICT"] = "1";
            }

            using var process = System.Diagnostics.Process.Start(startInfo)
                ?? throw new InvalidOperationException("`bash` did not start.");

            // Both pipes drained concurrently, then the wait — reading one to the end first
            // deadlocks the moment the hook fills the other.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            process.WaitForExit(milliseconds: 30_000).Should().BeTrue("the hook reads one message");
            Task.WaitAll(stdout, stderr);

            return process.ExitCode;
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The code of CI's commit-hygiene step: its <c>run:</c> block, comments removed.
    /// </summary>
    /// <remarks>
    /// Comments out, because the step explains in prose what it no longer does — and a
    /// check reading prose would pass a step whose only mention of the hook is a comment.
    /// </remarks>
    private static string CommitHygieneStepCode()
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryPaths.RepoRoot(), ".github", "workflows", "ci.yml"));

        var start = workflow.IndexOf("- name: Commit hygiene", StringComparison.Ordinal);
        start.Should().BePositive("the meta job carries a commit-hygiene step");

        var end = workflow.IndexOf("- name: ", start + 1, StringComparison.Ordinal);
        var step = workflow[start..(end < 0 ? workflow.Length : end)];

        return string.Join('\n', step.Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#')));
    }

    /// <summary>The types the hook's grammar admits, from its leading alternation.</summary>
    private static List<string> GrammarTypes(string hook)
    {
        var types = Regex.Match(hook, @"grep -qE '\^\(([a-z|]+)\)").Groups[1].Value
            .Split('|', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        types.Should().NotBeEmpty("a grammar this test cannot find is a grammar it cannot compare");

        return types;
    }

    /// <summary>The types Standards 14's table under § Commits names.</summary>
    private static List<string> StandardTypes()
    {
        var standard = File.ReadAllText(
            Path.Combine(RepositoryPaths.RepoRoot(), "docs", "standards", "14-git-workflow.md"));

        var start = standard.IndexOf("\n## Commits", StringComparison.Ordinal);
        start.Should().BePositive("Standards 14 keeps its commit rules under § Commits");

        // To the next heading of either level: a slice ending only at `###` would run on
        // into § Pull Requests the day § Trailers moved.
        var next = Regex.Match(standard[(start + 1)..], @"\n#{2,3} ");
        var section = next.Success ? standard.Substring(start, next.Index + 1) : standard[start..];

        var types = Regex
            .Matches(section, @"^\|\s*`([a-z]+)`\s*\|", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToList();

        types.Should().NotBeEmpty("a table this test cannot read is a table it cannot compare");

        return types;
    }
}
