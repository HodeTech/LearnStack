using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class PersistenceConventionTests
{
    // These owners retain independent operations, never concurrent work on the
    // ambient connection. Only the named invocation spans are omitted. Their
    // actual publication/cleanup/capacity proof remains in the recurrence IL and
    // PostgreSQL guards, rather than becoming a whole-file source exemption.
    private sealed record RecurrenceParallelSite(string Type, string Method, bool Internal,
        string Expression, int Count, string? SchedulingBlock = null);

    private static readonly Dictionary<string, RecurrenceParallelSite[]> RecurrenceParallelSites = new(StringComparer.Ordinal)
    {
        ["LearnStack.Infrastructure/Jobs/HangfireRecurrenceClient.cs"] =
        [new("BackgroundJobNativeRecurrenceOperation", "Start", true, "Task.Run(StartThread)", 2,
            """
            { Task startup;
              if (ExecutionContext.IsFlowSuppressed()) startup = Task.Run(StartThread);
              else { using (ExecutionContext.SuppressFlow()) startup = Task.Run(StartThread); }
              _published.TrySetResult(startup); }
            """)],
        ["LearnStack.Infrastructure/Jobs/BackgroundJobRecipientOperation.cs"] =
        [
            new("BackgroundJobRecipientOperation", "Start", true, "Task.Run(ExecuteApplicationAsync)", 2,
                """
                { _issued.TrySetResult(); Task<JobRecipientSubmissionResult> actual;
                  if (ExecutionContext.IsFlowSuppressed()) actual = Task.Run(ExecuteApplicationAsync);
                  else { using (ExecutionContext.SuppressFlow()) actual = Task.Run(ExecuteApplicationAsync); }
                  _published.TrySetResult(actual); }
                """),
            new("BackgroundJobRecipientOperation", "WaitAsync", false, "Task.WhenAny(_returned.Task, _stop.Task, delay)", 1),
            new("BackgroundJobRecipientOperation", "ObserveAsync", false, "Task.WhenAll(_wait, _cancel)", 1),
        ],
        ["LearnStack.Infrastructure/Jobs/BackgroundJobRecurrenceControl.cs"] =
        [
            new("BackgroundJobRecurrenceControl", "Start", true, "Task.Run(RunAsync)", 2,
                """
                { Task loop;
                  if (_registry.Keys.Count == 0) loop = Task.CompletedTask;
                  else if (ExecutionContext.IsFlowSuppressed()) loop = Task.Run(RunAsync);
                  else { using (ExecutionContext.SuppressFlow()) loop = Task.Run(RunAsync); }
                  _loopPublished.TrySetResult(loop); }
                """),
            new("BackgroundJobRecurrenceControl", "ObserveCompletionAsync", false, "Task.WhenAll(actual)", 1),
        ],
    };

    private static string? RecurrenceParallelScanText(string code, RecurrenceParallelSite[] sites)
    {
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        if (root.ContainsDiagnostics) return null;
        var omitted = new List<InvocationExpressionSyntax>();
        foreach (var site in sites)
        {
            var types = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(type => TypeIdentity(type) == "LearnStack.Infrastructure.Jobs." + site.Type + "`0").ToArray();
            if (types.Length != 1) return null;
            var methods = types[0].Members.OfType<MethodDeclarationSyntax>()
                .Where(method => method.Identifier.ValueText == site.Method).ToArray();
            if (methods.Length != 1 || methods[0].ParameterList.Parameters.Count != 0 || methods[0].TypeParameterList is not null
                || methods[0].Modifiers.Any(token => token.IsKind(SyntaxKind.StaticKeyword))
                || !methods[0].Modifiers.Any(token => token.IsKind(site.Internal ? SyntaxKind.InternalKeyword : SyntaxKind.PrivateKeyword))
                || methods[0].Modifiers.Any(token => token.IsKind(SyntaxKind.AsyncKeyword)) == site.Internal
                || !SyntaxFactory.AreEquivalent(methods[0].ReturnType, SyntaxFactory.ParseTypeName(site.Internal ? "void" : "Task"))) return null;
            if (site.SchedulingBlock is { } block && methods[0].Body?.Statements.OfType<TryStatementSyntax>()
                    .Count(statement => SyntaxFactory.AreEquivalent(statement.Block, SyntaxFactory.ParseStatement(block))) != 1) return null;
            var expression = SyntaxFactory.ParseExpression(site.Expression);
            var calls = methods[0].DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(call => SyntaxFactory.AreEquivalent(call, expression)).ToArray();
            if (calls.Length != site.Count) return null;
            omitted.AddRange(calls);
        }
        foreach (var call in omitted.OrderByDescending(call => call.SpanStart))
            code = code.Remove(call.SpanStart, call.Span.Length).Insert(call.SpanStart, "default!");
        return code;
    }

    [Fact]
    public void Recurrence_parallel_allowance_is_limited_to_exact_owned_expressions_and_publication()
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), "learnstack-recurrence-parallel-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (path, sites) in RecurrenceParallelSites)
            {
                var production = File.ReadAllText(Path.Combine(RepositoryPaths.BackendSrc(), path));
                var file = Path.Combine(sourceRoot, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                Check(production, false);
                foreach (var site in sites)
                {
                    Check(production.Replace(site.Expression, "Task.WhenAll(unowned)", StringComparison.Ordinal), true);
                    Check(production.Replace(site.Method + "(", "UnapprovedMethod(", StringComparison.Ordinal), true);
                    Check(production.Replace("class " + site.Type, "class OtherOwner", StringComparison.Ordinal), true);
                }
                foreach (var original in new[] { "ExecutionContext.SuppressFlow()", "_published.TrySetResult(", "_loopPublished.TrySetResult(" })
                    if (production.Contains(original, StringComparison.Ordinal))
                        Check(production.Replace(original, original.StartsWith("ExecutionContext", StringComparison.Ordinal)
                            ? "ExecutionContext.Capture()" : "_unowned.TrySetResult(", StringComparison.Ordinal), true);
                Check(production.Replace("TrySetResult(actual)", "TrySetResult(Task.CompletedTask)", StringComparison.Ordinal)
                    .Replace("TrySetResult(startup)", "TrySetResult(Task.CompletedTask)", StringComparison.Ordinal)
                    .Replace("TrySetResult(loop)", "TrySetResult(Task.CompletedTask)", StringComparison.Ordinal), true);
                Check(production + "\ninternal sealed class ExtraParallel { private async Task RunAsync() { await Task.WhenAll(first, second); } }", true);
                Check(production + "\ninternal sealed class ExtraStored { private void Run() { var work = store.ReadAsync(ct); } }", true);
                var starter = CSharpSyntaxTree.ParseText(production).GetRoot().DescendantNodes()
                    .OfType<MethodDeclarationSyntax>().Single(method => method.Identifier.ValueText == sites[0].Method);
                Check(production.Insert(starter.Body!.OpenBraceToken.Span.End, " Task.WhenAll(extra);"), true);
                Check(production + "\ninternal sealed class Sequential { private async Task RunAsync() { await first; await second; } }", false);
                File.WriteAllText(file, production);
                var borrowed = Path.Combine(sourceRoot, "LearnStack.Infrastructure/Unapproved/" + Path.GetFileName(path));
                Directory.CreateDirectory(Path.GetDirectoryName(borrowed)!);
                File.WriteAllText(borrowed, production);
                ParallelOffenders(sourceRoot, [borrowed]).Should().Equal([Path.GetRelativePath(sourceRoot, borrowed).Replace('\\', '/')]);
                ParallelOffenders(sourceRoot, [file]).Should().BeEmpty("restoration retains all nine approved expressions without lending file permission");

                void Check(string code, bool dirty)
                {
                    if (dirty) code.Should().NotBe(production, "every control must alter an actual production subject");
                    File.WriteAllText(file, code);
                    var offenders = ParallelOffenders(sourceRoot, [file]);
                    if (dirty) offenders.Should().Equal([path]);
                    else offenders.Should().BeEmpty();
                }
            }
        }
        finally { if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, recursive: true); }
    }
}
