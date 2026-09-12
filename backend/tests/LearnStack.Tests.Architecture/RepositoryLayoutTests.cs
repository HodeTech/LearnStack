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
    /// Standards 14 § Commits: one script — the <c>commit-msg</c> hook — states the
    /// commit-subject rule. CI's commit-hygiene step runs that same hook, and the
    /// standard's table names the types it admits.
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
            .And.NotContain("feat", "a type list in CI is a second grammar — the drift this rule exists to prevent");

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

        // A pasted log with no blank line is one ten-megabyte paragraph. The hook refuses it
        // inside the timeout; joining the lines by appending took 38 seconds, measured.
        var pasted = string.Join('\n', Enumerable.Range(0, 200_000).Select(i => $"line {i} {new string('x', 48)}"));
        RunCommitMsgHook(hookPath, pasted, editorRan: false, strict: true).Should().Be(1);

        // And CI's step itself, run as CI runs it, on a repository of three commits: a
        // refused subject fails the step and is named, an admitted one is not, and a base
        // that does not resolve fails rather than reporting success on nothing.
        using var repository = new ScratchRepository(hookPath);
        var baseSha = repository.Commit("feat: the base the pull request branches from");
        var good = repository.Commit("fix: an admitted subject");
        var bad = repository.Commit("Update the readme");

        var (refused, refusedOutput) = repository.RunCommitHygiene(baseSha, bad);
        refused.Should().Be(1, "{0}", refusedOutput);
        refusedOutput.Should().Contain($"::error::{bad[..7]}").And.NotContain(good[..7]);

        var (admitted, admittedOutput) = repository.RunCommitHygiene(baseSha, good);
        admitted.Should().Be(0, "{0}", admittedOutput);

        var (unresolved, unresolvedOutput) = repository.RunCommitHygiene(new string('0', 40), good);
        unresolved.Should().Be(1, "{0}", unresolvedOutput);
        unresolvedOutput.Should().Contain("::error::could not read");
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

            var environment = new Dictionary<string, string?>
            {
                ["GIT_EDITOR"] = editorRan ? null : ":",
                ["LEARNSTACK_COMMIT_MSG_STRICT"] = strict ? "1" : null,
            };

            return Run("bash", directory.FullName, environment, hookPath, file).ExitCode;
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Runs a program with the global and system git configuration switched off and
    /// returns its exit code and combined output.
    /// </summary>
    /// <remarks>
    /// An environment value of <see langword="null"/> removes the variable, so a
    /// developer's own <c>GIT_EDITOR</c> cannot leak into a case that means "no editor".
    /// </remarks>
    private static (int ExitCode, string Output) Run(
        string program,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(program)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";

        foreach (var (name, value) in environment)
        {
            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException($"`{program}` did not start.");

        // Both pipes drained concurrently, then the wait — reading one to the end first
        // deadlocks the moment the child fills the other.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        // Killed before the assertion, not after it: a failing assertion throws, the caller
        // deletes the scratch directory in its `finally`, and a child still running in that
        // directory turns one clear failure into a second, unrelated one.
        var exited = process.WaitForExit(milliseconds: 30_000);

        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }

        exited.Should().BeTrue("`{0}` finishes in seconds", program);
        Task.WaitAll(stdout, stderr);

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    /// <summary>
    /// A throwaway git repository carrying a copy of the hook, for running CI's
    /// commit-hygiene step the way the runner does.
    /// </summary>
    private sealed class ScratchRepository : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("learnstack-commit-hygiene");

        private readonly Dictionary<string, string?> _identity = new()
        {
            ["GIT_AUTHOR_NAME"] = "probe",
            ["GIT_AUTHOR_EMAIL"] = "probe@example.invalid",
            ["GIT_COMMITTER_NAME"] = "probe",
            ["GIT_COMMITTER_EMAIL"] = "probe@example.invalid",
        };

        public ScratchRepository(string hookPath)
        {
            Git("init", "-q");

            // In the working tree, where the step looks for it — and not installed as a
            // hook, so the refused subject below can be committed at all.
            var hooks = Directory.CreateDirectory(Path.Combine(_directory.FullName, ".githooks"));
            File.Copy(hookPath, Path.Combine(hooks.FullName, "commit-msg"));
        }

        public string Commit(string subject)
        {
            Git("commit", "-q", "--allow-empty", "-m", subject);
            return Git("rev-parse", "HEAD").Trim();
        }

        public (int ExitCode, string Output) RunCommitHygiene(string baseSha, string headSha)
        {
            var temp = Directory.CreateDirectory(Path.Combine(_directory.FullName, ".runner-temp"));

            var environment = new Dictionary<string, string?>(_identity)
            {
                ["BASE_SHA"] = baseSha,
                ["HEAD_SHA"] = headSha,
                ["RUNNER_TEMP"] = temp.FullName,
            };

            return Run("bash", _directory.FullName, environment, "-c", CommitHygieneScript());
        }

        public void Dispose() => _directory.Delete(recursive: true);

        private string Git(params string[] arguments)
        {
            var (exitCode, output) = Run("git", _directory.FullName, _identity, arguments);
            exitCode.Should().Be(0, "git {0}: {1}", string.Join(' ', arguments), output);
            return output;
        }
    }

    /// <summary>The text of CI's commit-hygiene step, from its name to the next step.</summary>
    private static string CommitHygieneStep()
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryPaths.RepoRoot(), ".github", "workflows", "ci.yml"));

        var start = workflow.IndexOf("- name: Commit hygiene", StringComparison.Ordinal);
        start.Should().BePositive("the meta job carries a commit-hygiene step");

        var end = workflow.IndexOf("- name: ", start + 1, StringComparison.Ordinal);
        return workflow[start..(end < 0 ? workflow.Length : end)];
    }

    /// <summary>The step's code, comments removed.</summary>
    /// <remarks>
    /// Comments out, because the step explains in prose what it no longer does — and a
    /// check reading prose would pass a step whose only mention of the hook is a comment.
    /// </remarks>
    private static string CommitHygieneStepCode() =>
        string.Join('\n', CommitHygieneStep().Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#')));

    /// <summary>The step's <c>run:</c> block, de-indented into the script bash runs.</summary>
    private static string CommitHygieneScript()
    {
        var lines = CommitHygieneStep().Split('\n');
        var run = Array.FindIndex(lines, line => line.TrimStart().StartsWith("run: |", StringComparison.Ordinal));
        run.Should().BePositive("the commit-hygiene step runs a script");

        var body = lines.Skip(run + 1).ToList();
        var indent = body.First(line => line.Trim().Length > 0).TakeWhile(c => c == ' ').Count();

        return string.Join('\n', body
            .TakeWhile(line => line.Trim().Length == 0 || line.TakeWhile(c => c == ' ').Count() >= indent)
            .Select(line => line.Length >= indent ? line[indent..] : string.Empty));
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
