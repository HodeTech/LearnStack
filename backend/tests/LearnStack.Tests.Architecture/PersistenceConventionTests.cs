using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Application.Pipeline;
using LearnStack.Application.Persistence;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The persistence rules
/// <see href="../../../docs/decisions/0039-optimistic-concurrency-token.md">ADR-0039</see>
/// and
/// <see href="../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040</see>
/// assign to Packet 6, catalogued in
/// <see href="../../../docs/standards/21-architecture-tests-catalogue.md">Standards 21
/// § Persistence: concurrency and the unit of work</see>.
/// </summary>
/// <remarks>
/// Model inspection rather than a source scan. The mistake these rules exist to
/// catch is not a forbidden call site — it is EF metadata that looks right at the
/// call site and is wrong in the model, which is exactly what a scan cannot see.
/// </remarks>
public sealed partial class PersistenceConventionTests
{
    [Fact]
    public void Aggregates_With_Optimistic_Concurrency_Map_RowVersion()
    {
        // Three properties of the metadata, not one, because the two ways of
        // getting this wrong fail different ones:
        //
        //   IsRowVersion() / ValueGeneratedOnAddOrUpdate() leave both save
        //   behaviours at Ignore, and EF then omits row_version from the UPDATE
        //   entirely — the token stays 0 for the life of the row and every lost
        //   update succeeds while reporting success (ADR-0039 Amendment 1).
        //
        //   HasDefaultValue(0L) — needed for the DDL template's DEFAULT 0 — leaves
        //   ValueGenerated at OnAdd on its own. That one is benign today and is
        //   still rejected: it is a store-generated declaration on a column the
        //   aggregate increments, and the next reader of the model has to work out
        //   which of the two mistakes it is. `.ValueGeneratedNever()` states it
        //   (ADR-0039 Amendment 2).
        // Every module's model, not one: read against Tenancy alone this rule said
        // nothing about the four aggregates the second module shipped, while
        // Standards 21 and ADR-0039 both describe it as covering every entity that
        // implements IOptimisticConcurrency. Measured — the forbidden IsRowVersion()
        // form passed on both new aggregates.
        var contexts = Modules.Scoped.Select(module => module.Context()).ToList();

        try
        {
            AssertRowVersionMapping(contexts);
        }
        finally
        {
            contexts.ForEach(context => context.Dispose());
        }
    }

    private static void AssertRowVersionMapping(IReadOnlyCollection<DbContext> contexts)
    {
        var offenders = new List<string>();

        foreach (var entity in contexts.SelectMany(context => context.Model.GetEntityTypes()))
        {
            if (!typeof(IOptimisticConcurrency).IsAssignableFrom(entity.ClrType))
            {
                continue;
            }

            var version = entity.FindProperty(nameof(IOptimisticConcurrency.Version));

            if (version is null
                || version.GetColumnName() != "row_version"
                || !version.IsConcurrencyToken
                || version.ValueGenerated != ValueGenerated.Never
                || version.GetBeforeSaveBehavior() != PropertySaveBehavior.Save
                || version.GetAfterSaveBehavior() != PropertySaveBehavior.Save)
            {
                offenders.Add(
                    $"{entity.ClrType.Name}: column={version?.GetColumnName() ?? "<unmapped>"} "
                    + $"token={version?.IsConcurrencyToken} valueGenerated={version?.ValueGenerated} "
                    + $"before={version?.GetBeforeSaveBehavior()} after={version?.GetAfterSaveBehavior()}");
            }
        }

        offenders.Should().BeEmpty(
            "row_version is IsConcurrencyToken() + ValueGeneratedNever(), and nothing "
            + "that tells EF the database generates it (ADR-0039; Standards 05 § Concurrency)");

        // A model with no IOptimisticConcurrency entity would pass the loop above
        // without inspecting anything, which is the same defect as an inclusion
        // list that matches nothing — and it is asserted per model, because one
        // model carrying aggregates satisfies a total however many carry none.
        foreach (var context in contexts)
        {
            context.Model.GetEntityTypes()
                .Count(e => typeof(IOptimisticConcurrency).IsAssignableFrom(e.ClrType))
                .Should().BeGreaterThan(
                    0,
                    $"{context.GetType().Name} must be a model that has aggregates in it");
        }
    }

    [Fact]
    public void Migration_Startup_Project_References_EntityFrameworkCore_Design()
    {
        // `dotnet ef` resolves the design package from the STARTUP project, and
        // `make migrate` names LearnStack.Api. Without the reference the tool
        // refuses before it opens a connection — "Your startup project
        // 'LearnStack.Api' doesn't reference Microsoft.EntityFrameworkCore.Design"
        // — and Packet 6 shipped a migration in exactly that state: green under
        // Testcontainers, which calls Database.MigrateAsync() directly, and
        // inapplicable by the one path Standards 05 § Database roles documents.
        var startupProject = Path.Combine(
            RepositoryPaths.BackendSrc(), "LearnStack.Api", "LearnStack.Api.csproj");

        var references = XDocument.Load(startupProject)
            .Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .ToList();

        references.Should().Contain("Microsoft.EntityFrameworkCore.Design",
            "`make migrate` passes --startup-project backend/src/LearnStack.Api, and "
            + "dotnet ef resolves the design-time package from there");
    }

    [Fact]
    public void Module_DbContexts_Enlist_In_The_Ambient_UnitOfWork()
    {
        // Two halves, because either alone leaves the hole open.
        //
        // The registration half: the composition root's own persistence
        // registration is run, and every DbContext service in it must be one the
        // shared helper registered. A context registered any other way is absent
        // from this collection's marker — read off the collection, so the answer
        // is about the container built here and not about anything another test
        // in this process happened to register first.
        var services = new ServiceCollection();
        services.AddLearnStackPersistence(new ConfigurationBuilder().Build());

        var registered = services.RegisteredContexts();

        var contexts = services
            .Where(descriptor => typeof(DbContext).IsAssignableFrom(descriptor.ServiceType))
            .ToList();

        contexts.Should().NotBeEmpty("TenancyDbContext is registered and is the first consumer");

        contexts.Should().OnlyContain(
            descriptor => registered.Contains(descriptor.ServiceType),
            "every DbContext registration goes through AddModuleDbContext");

        contexts.Should().OnlyContain(
            descriptor => descriptor.Lifetime == ServiceLifetime.Scoped
                          && descriptor.ImplementationFactory != null,
            "a context is built per scope, from the connection IUnitOfWork owns — "
            + "a type registration would let EF open its own");

        // The call-site half: a context on its own connection never saw
        // SET LOCAL, so every read through it returns zero rows under the
        // corrected policy — silently.
        //
        // Nine files under backend/src may reach for a connection at all: the five
        // design-time factories, where a connection string is the point — one per
        // migration chain, and a module that ships a schema ships one; the shared
        // module helper, which passes a connection rather than a string; the API's
        // separately guarded platform-role builder; and the shared application-role
        // builder used by both runtime hosts; and ADR-0046's confined dispatcher
        // builder. Another site requires a new decision, not a wildcard exemption.
        //
        // The scan covers the raw constructors as well as `UseNpgsql` and
        // `AddDbContext`, because a call site that opened its own
        // `NpgsqlConnection` would bypass the seam without naming either.
        var callSites = Directory
            .EnumerateFiles(RepositoryPaths.BackendSrc(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                       StringComparison.Ordinal)
                       && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                       StringComparison.Ordinal))
            .Where(file => ProviderTokens.Any(token =>
                StripComments(File.ReadAllText(file)).Contains(token, StringComparison.Ordinal)))
            // Qualified by the directory above the file, not the bare filename. There
            // are now two `Program.cs` under backend/src — the API's and the seeder's —
            // and a set keyed on filenames alone would let one of them acquire a
            // connection under cover of the other's entry.
            .Select(file => $"{Path.GetFileName(Path.GetDirectoryName(file))}/{Path.GetFileName(file)}")
            .Order(StringComparer.Ordinal)
            .ToList();

        callSites.Should().BeEquivalentTo(
        [
            "Persistence/ModuleDbContextRegistration.cs",
            "Composition/PersistenceCompositionExtensions.cs",
            "Persistence/PlatformDbContextFactory.cs",
            "Persistence/TenancyDbContextFactory.cs",

            // One design-time factory per migration chain. Customization's is the
            // third, Audit's the fourth and Education's the fifth. All are here
            // rather than exempted: the list makes the next connection site a
            // reviewed diff.
            "Persistence/CustomizationDbContextFactory.cs",
            "Persistence/AuditDbContextFactory.cs",
            "Persistence/EducationDbContextFactory.cs",

            // Both runtime hosts delegate application-role construction to this
            // shared guard; the API retains its separate platform-role builder.
            "Persistence/ApplicationDataSource.cs",
            "Messaging/OutboxDispatcherDataSource.cs",
        ]);
    }

    /// <summary>
    /// Every way source under <c>backend/src</c> could configure or open a
    /// PostgreSQL connection outside the seam.
    /// </summary>
    private static readonly string[] ProviderTokens =
    [
        "UseNpgsql",
        "AddDbContext",
        "NpgsqlDataSourceBuilder",
        "NpgsqlDataSource.Create",
        "new NpgsqlConnection(",
    ];

    [Fact]
    public void TransactionBehavior_Does_Not_Reference_A_Module_Assembly()
    {
        // The seam exists so that the behavior owning the commit boundary never
        // has to name a module. Two assertions: the assembly takes no build-time
        // reference to one, and the behavior delegates to a coordinator whose
        // surface names IUnitOfWork and no DbContext.
        // The PROJECT file, not only the emitted assembly-reference table. The
        // compiler elides a reference whose types the IL never touches, so an
        // unused <ProjectReference> to a module would leave a reflection-only
        // check green — a trap this repository has documented twice and moved a
        // rule out of this project over.
        var project = Path.Combine(
            RepositoryPaths.BackendSrc(), "LearnStack.Application", "LearnStack.Application.csproj");

        XDocument.Load(project)
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .Should().NotContain(
                include => include.Contains("LearnStack.Modules.", StringComparison.Ordinal),
                "LearnStack.Application is generic over every module and references none");

        var application = typeof(TransactionBehavior<,>).Assembly;

        var referenced = application.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .ToList();

        // The positive control the assembly half needs: if this list were empty
        // the NotContain below would pass against a check that read nothing.
        referenced.Should().Contain("LearnStack.SharedKernel");

        referenced.Should().NotContain(
            name => name.StartsWith("LearnStack.Modules.", StringComparison.Ordinal));

        var constructor = typeof(TransactionBehavior<,>).GetConstructors().Single();

        constructor.GetParameters().Select(parameter => parameter.ParameterType)
            .Should().Equal(typeof(TransactionExecutionCoordinator), typeof(IBackgroundJobRequestCatalog));
        typeof(TransactionExecutionCoordinator).GetConstructors().Single()
            .GetParameters().Select(parameter => parameter.ParameterType)
            .Should().Contain(typeof(IUnitOfWork))
            .And.NotContain(parameter => typeof(DbContext).IsAssignableFrom(parameter));
    }

    [Fact]
    public void Modules_Do_Not_Parallelize_Over_The_Ambient_Connection()
    {
        // ADR-0040: one connection per scope, and every module DbContext enlisted on it — so
        // one command at a time. Two operations handed to Task.WhenAll run on that one
        // connection concurrently and corrupt the protocol, and a DbContext refuses a second
        // operation while one is in flight. No module needs to fan out today, so the rule is
        // stricter than its ADR sentence: which operations are DbContext-bound cannot be seen
        // from source, and the honest rule is that module code does not fan out at all. A
        // fan-out that touches no connection is added to ParallelSites with its reason.
        // Shared application/infrastructure code also owns the scoped producer, collector,
        // audit capture and transaction lifecycle, so it participates in the same census.
        var sourceRoot = RepositoryPaths.BackendSrc();
        var files = AmbientSourceFiles(sourceRoot);

        files.Should().Contain(file => file.EndsWith("CommandHandler.cs", StringComparison.Ordinal),
            "the premise: the scan reads the module handlers that open the unit of work's work");
        var relativeFiles = files.Select(file => Path.GetRelativePath(sourceRoot, file).Replace('\\', '/')).ToArray();
        relativeFiles.Should().Contain([
            "LearnStack.Application/Messaging/ProducerExecution.cs",
            "LearnStack.Application/Messaging/DomainEventCollector.cs",
            "LearnStack.Infrastructure.Audit/AuditStateCapture.cs",
        ], "the census includes shared scoped execution state, not only module callers");
        ParallelSites.Keys.Concat(RetainedLoaderSites.Keys).Concat(RecurrenceParallelSites.Keys).Should().BeSubsetOf(relativeFiles,
            "an exception must name a real scanned source file");

        var offenders = ParallelOffenders(sourceRoot, files);
        offenders.Should().BeEmpty(
            "modules and shared execution code run one operation at a time on the ambient connection (ADR-0040 § Nesting); offenders: {0}",
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void The_parallelism_census_reaches_shared_code_and_bounds_its_singleton_exceptions()
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), "learnstack-parallel-census-" + Guid.NewGuid().ToString("N"));
        string[] forbidden =
        [
            "Modules/Probe/Application/CommandHandler.cs",
            "LearnStack.Application/Messaging/ProducerExecution.cs",
            "LearnStack.Infrastructure/Messaging/DomainEventSaveChangesInterceptor.cs",
            "LearnStack.Infrastructure.Audit/AuditStateCapture.cs",
            "Modules/Probe/Infrastructure/Caching/InMemoryCacheService.cs",
            "Modules/Probe/Infrastructure/MultiTenancy/CachedHostToTenantResolver.cs",
        ];
        string[] ignored =
        [
            "LearnStack.Application/obj/Generated.cs",
            "LearnStack.Infrastructure/bin/Generated.cs",
        ];
        try
        {
            foreach (var path in forbidden.Concat(ignored).Concat(ParallelSites.Keys))
            {
                var file = Path.Combine(sourceRoot, path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "await Task.WhenAll(first, second);");
            }

            var files = AmbientSourceFiles(sourceRoot);
            ParallelOffenders(sourceRoot, files).Should().BeEquivalentTo(forbidden,
                "the same production census rejects fan-out in every shared execution project and cannot lend singleton exceptions to another path");
            ParallelSites.Values.Should().OnlyContain(reason => !string.IsNullOrWhiteSpace(reason));
            File.WriteAllText(Path.Combine(sourceRoot, forbidden[1]), "await first; await second;");
            ParallelOffenders(sourceRoot, files).Should().BeEquivalentTo(forbidden.Where(path => path != forbidden[1]),
                "sequential shared execution remains legal under the same predicate");

            foreach (var (relative, site) in RetainedLoaderSites)
            {
                var production = File.ReadAllText(Path.Combine(RepositoryPaths.BackendSrc(), relative));
                var probe = Path.Combine(sourceRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(probe)!);
                File.WriteAllText(probe, production);
                ParallelOffenders(sourceRoot, [probe]).Should().BeEmpty(
                    "the exact private factory owns one independent connection and retains its actual task before returning it");
                string[] dirty =
                [
                    production.Replace("JobApplicationSourceOwner.RefuseExternalEntry();", "", StringComparison.Ordinal),
                    production.Replace("resources.HoldActualTask(actual);", "", StringComparison.Ordinal),
                    production.Replace("resources.HoldActualTask(actual);", "resources.HoldActualTask(Task.CompletedTask);", StringComparison.Ordinal),
                    production.Replace("return actual;", "return Task.CompletedTask;", StringComparison.Ordinal),
                    production.Replace(site.Source + ".Value.CreateConnection()", "unit.Connection", StringComparison.Ordinal),
                    production.Replace("resources?.HoldConnection(connection);", "", StringComparison.Ordinal),
                    production.Replace("await resources.CleanupAsync().ConfigureAwait(false);", "await unit.DisposeAsync();", StringComparison.Ordinal),
                    production + "\ninternal sealed class ExtraParallelProbe { private async Task RunAsync() { await Task.WhenAll(first, second); } }",
                    production + "\ninternal sealed class ExtraStoredProbe { private void Run() { var rows = store.ReadAsync(ct); } }",
                ];
                foreach (var planted in dirty)
                {
                    planted.Should().NotBe(production, "every dirty control must change the actual approved subject");
                    File.WriteAllText(probe, planted);
                    ParallelOffenders(sourceRoot, [probe]).Should().Equal([relative],
                        "the exception cannot lose actual ownership or lend permission to another operation in the same file");
                }
                File.WriteAllText(probe, production +
                    "\ninternal sealed class SequentialProbe { private async Task RunAsync() { await first; await second; } }");
                ParallelOffenders(sourceRoot, [probe]).Should().BeEmpty(
                    "ordinary sequential work elsewhere in the same file remains legal");
                File.WriteAllText(probe, production.Replace(site.Starter + "(", "RemovedStarter(", StringComparison.Ordinal));
                ParallelOffenders(sourceRoot, [probe]).Should().Equal([relative],
                    "a missing retained-loader subject cannot make the exception vacuous");
            }
        }
        finally
        {
            if (Directory.Exists(sourceRoot)) Directory.Delete(sourceRoot, recursive: true);
        }
    }

    private static readonly string[] AmbientSourceProjects =
        ["Modules", "LearnStack.Application", "LearnStack.Infrastructure", "LearnStack.Infrastructure.Audit"];

    private static string[] AmbientSourceFiles(string sourceRoot) =>
        AmbientSourceProjects
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(sourceRoot, project), "*.cs", SearchOption.AllDirectories))
            .Where(file => !Path.GetRelativePath(sourceRoot, file).Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"))
            .ToArray();

    private static string[] ParallelOffenders(string sourceRoot, IEnumerable<string> files) =>
        files.Select(file => (Path: Path.GetRelativePath(sourceRoot, file).Replace('\\', '/'),
                Code: StripComments(File.ReadAllText(file))))
            .Where(file => !ParallelSites.ContainsKey(file.Path))
            .Where(file => RetainedLoaderSites.TryGetValue(file.Path, out var site)
                ? RetainedLoaderScanText(file.Code, site) is not { } scan || FansOut(scan)
                : RecurrenceParallelSites.TryGetValue(file.Path, out var recurrence)
                    ? RecurrenceParallelScanText(file.Code, recurrence) is not { } retained || FansOut(retained)
                : FansOut(file.Code))
            .Select(file => file.Path).ToArray();

    // These four private starters retain one actual auxiliary operation before a cache or
    // audit caller can observe its Task. They use an independent connection, never the
    // ambient unit. Only that validated initializer is omitted from the fan-out scan;
    // every other statement in each file still participates (ADR-0040/0047).
    private sealed record RetainedLoaderSite(string Type, string Starter, string Core, string Source,
        string Cancellation, string Parameters, string StarterBody, string Cleanup, string[] Helpers);

    private static readonly Dictionary<string, RetainedLoaderSite> RetainedLoaderSites = new(StringComparer.Ordinal)
    {
        ["LearnStack.Infrastructure.Audit/AuditConfigService.cs"] = new(
            "LearnStack.Infrastructure.Audit.AuditConfigService`0", "StartLoadAsync", "LoadAsync", "dataSource",
            "cancellationToken", "TenantId tenantId, JobApplicationAuxiliaryResources? resources, CancellationToken cancellationToken",
            """
            { JobApplicationSourceOwner.RefuseExternalEntry();
              if (sourceOwner is null) return LoadAsync(tenantId, null, cancellationToken);
              var resources = sourceOwner.BeginAuditConfigLoad();
              var actual = LoadAsync(tenantId, resources, cancellationToken);
              resources.HoldActualTask(actual); return actual; }
            """, "CleanupUnownedAsync(reader, read, announce, readOnly, transaction, connection)", ["CleanupUnownedAsync"]),
        ["LearnStack.Infrastructure.Audit/PostgresAuditStore.cs"] = new(
            "LearnStack.Infrastructure.Audit.PostgresAuditStore`0", "WriteOwnTransactionAsync", "WriteOwnTransactionCoreAsync", "_dataSource",
            "cancellationToken", "AuditEntryDraft entry, LearnStack.SharedKernel.Jobs.JobApplicationAuxiliaryResources? resources, CancellationToken cancellationToken",
            """
            { JobApplicationSourceOwner.RefuseExternalEntry();
              var owner = LearnStack.SharedKernel.Jobs.JobApplicationSourceOwner.Current;
              if (owner is null) return WriteOwnTransactionCoreAsync(entry, null, cancellationToken);
              var resources = owner.BeginStandaloneAudit();
              var actual = WriteOwnTransactionCoreAsync(entry, resources, cancellationToken);
              resources.HoldActualTask(actual); return actual; }
            """, "CleanupUnownedAsync(command, announce, transaction, connection)", ["CleanupUnownedAsync", "Add", "Bind"]),
        ["Modules/Tenancy/LearnStack.Modules.Tenancy.Infrastructure/FeatureFlags.cs"] = new(
            "LearnStack.Modules.Tenancy.Infrastructure.FeatureFlags`0", "StartTenantFlagsLoadAsync", "LoadTenantFlagsAsync", "dataSource",
            "ct", "TenantId tenantId, JobApplicationAuxiliaryResources? resources, CancellationToken ct",
            """
            { JobApplicationSourceOwner.RefuseExternalEntry();
              if (sourceOwner is null) return LoadTenantFlagsAsync(tenantId, null, ct);
              var resources = sourceOwner.BeginTenantFlagsLoad();
              var actual = LoadTenantFlagsAsync(tenantId, resources, ct);
              resources.HoldActualTask(actual); return actual; }
            """, "CleanupUnownedAsync(reader, read, announce, readOnly, transaction, connection)", ["CleanupUnownedAsync"]),
        ["Modules/Tenancy/LearnStack.Modules.Tenancy.Infrastructure/KillswitchOverlay.cs"] = new(
            "LearnStack.Modules.Tenancy.Infrastructure.KillswitchOverlay`0", "StartLoadAsync", "LoadAsync", "dataSource",
            "ct", "JobApplicationAuxiliaryResources? resources, CancellationToken ct",
            """
            { JobApplicationSourceOwner.RefuseExternalEntry();
              if (sourceOwner is null) return LoadAsync(null, ct);
              var resources = sourceOwner.BeginKillswitchLoad();
              var actual = LoadAsync(resources, ct);
              resources.HoldActualTask(actual); return actual; }
            """, "CleanupUnownedAsync(reader, read, connection)", ["CleanupUnownedAsync"]),
    };

    private static string? RetainedLoaderScanText(string code, RetainedLoaderSite site)
    {
        var syntax = CSharpSyntaxTree.ParseText(code).GetRoot();
        var types = syntax.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Where(type => TypeIdentity(type) == site.Type).ToArray();
        if (syntax.ContainsDiagnostics || types.Length != 1) return null;
        var starters = types[0].Members.OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == site.Starter).ToArray();
        var cores = types[0].Members.OfType<MethodDeclarationSyntax>()
            .Where(method => method.Identifier.ValueText == site.Core).ToArray();
        if (starters.Length != 1 || cores.Length != 1 || !IsPrivate(starters[0]) || !IsPrivate(cores[0])
            || !SyntaxFactory.AreEquivalent(starters[0].Body, SyntaxFactory.ParseStatement(site.StarterBody))
            || !SyntaxFactory.AreEquivalent(cores[0].ParameterList, SyntaxFactory.ParseParameterList("(" + site.Parameters + ")"))
            || !cores[0].Modifiers.Any(token => token.IsKind(SyntaxKind.AsyncKeyword))
            || cores[0].Body is not { } body) return null;
        var owned = body.Statements.OfType<TryStatementSyntax>().ToArray();
        if (owned.Length != 1 || owned[0].Catches.Count != 0 || owned[0].Finally is not { } cleanup
            || owned[0].Block.Statements.Count < 3) return null;
        var prefix = new[] { "connection = " + site.Source + ".Value.CreateConnection();",
            "resources?.HoldConnection(connection);", "await connection.OpenAsync(" + site.Cancellation + ").ConfigureAwait(false);" };
        if (prefix.Where((statement, index) => !SyntaxFactory.AreEquivalent(owned[0].Block.Statements[index],
                SyntaxFactory.ParseStatement(statement))).Any()
            || !SyntaxFactory.AreEquivalent(cleanup.Block, SyntaxFactory.ParseStatement(
                "{ if (resources is not null) await resources.CleanupAsync().ConfigureAwait(false); else await "
                + site.Cleanup + ".ConfigureAwait(false); }"))) return null;
        var calls = body.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
        if (calls.Count(call => CalledName(call) == "CreateConnection") != 1
            || calls.Count(call => CalledName(call) is "OpenAsync" or "OpenConnectionAsync") != 1
            || body.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>().Any()
            || calls.Any(call => call.Expression is IdentifierNameSyntax name
                && !site.Helpers.Contains(name.Identifier.ValueText, StringComparer.Ordinal))) return null;
        // A receiver must be a concrete parameter/local, the one lazy source, or the
        // dictionary's comparer. No injected ambient unit/context/service locator can
        // supply SQL, and no callback can be smuggled into these exact parameters.
        var roots = cores[0].ParameterList.Parameters.Select(parameter => parameter.Identifier.ValueText)
            .Concat(body.DescendantNodes().OfType<VariableDeclaratorSyntax>().Select(variable => variable.Identifier.ValueText))
            .Concat(site.Helpers).Append(site.Source).Append("StringComparer").Append("string").ToHashSet(StringComparer.Ordinal);
        var names = roots.Concat(["var", "NpgsqlConnection", "NpgsqlTransaction", "NpgsqlCommand", "NpgsqlDataReader", "InsertSql"])
            .ToHashSet(StringComparer.Ordinal);
        if (body.DescendantNodes().OfType<IdentifierNameSyntax>().Any(name =>
                !(name.Parent is MemberAccessExpressionSyntax access && ReferenceEquals(access.Name, name))
                && !(name.Parent is MemberBindingExpressionSyntax binding && ReferenceEquals(binding.Name, name))
                && !names.Contains(name.Identifier.ValueText))) return null;
        if (body.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Any(access => !roots.Contains(ReceiverRoot(access)))
            || body.DescendantNodes().OfType<ConditionalAccessExpressionSyntax>().Any(access => !roots.Contains(ReceiverRoot(access)))
            || body.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Any(creation =>
                creation.Type is not IdentifierNameSyntax { Identifier.ValueText: "NpgsqlCommand" }
                && creation.Type is not GenericNameSyntax { Identifier.ValueText: "Dictionary" })) return null;
        var actual = starters[0].Body!.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(variable => variable.Identifier.ValueText == "actual").Initializer!.Value;
        return code.Remove(actual.SpanStart, actual.Span.Length).Insert(actual.SpanStart, "default!");

        static bool IsPrivate(MethodDeclarationSyntax method) =>
            method.Modifiers.Any(token => token.IsKind(SyntaxKind.PrivateKeyword));
        static string? CalledName(InvocationExpressionSyntax call) => call.Expression switch
        {
            IdentifierNameSyntax name => name.Identifier.ValueText,
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
            _ => null,
        };
        static string ReceiverRoot(ExpressionSyntax expression) => expression switch
        {
            PredefinedTypeSyntax { Keyword.ValueText: "string" } => "string",
            IdentifierNameSyntax name => name.Identifier.ValueText,
            MemberAccessExpressionSyntax access => ReceiverRoot(access.Expression),
            InvocationExpressionSyntax call => ReceiverRoot(call.Expression),
            ConditionalAccessExpressionSyntax access => ReceiverRoot(access.Expression),
            MemberBindingExpressionSyntax binding when binding.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault() is { } conditional
                => ReceiverRoot(conditional.Expression),
            ParenthesizedExpressionSyntax parenthesized => ReceiverRoot(parenthesized.Expression),
            _ => "<unapproved receiver>",
        };
    }

    [Fact]
    public void The_Parallelism_Scan_Can_Actually_Fail()
    {
        // No module fans out, so the rule above passes whether its pattern works or not.
        FansOut("await Task.WhenAll(first, second);").Should().BeTrue();
        FansOut("await WhenAny(tasks);").Should().BeTrue("a `using static` import reaches it unqualified");
        FansOut("await foreach (var task in Task.WhenEach(tasks)) { }").Should().BeTrue();
        FansOut("await Parallel.ForEachAsync(items, work);").Should().BeTrue();
        FansOut("await Parallel.ForAsync(0, count, work);").Should().BeTrue();
        FansOut("Parallel . Invoke(one, two);").Should().BeTrue();
        FansOut("var rows = items.AsParallel().Select(Map).ToList();").Should().BeTrue();
        FansOut("var work = Task.Run(() => context.Items.ToListAsync(ct));").Should().BeTrue(
            "a task started on the pool runs on the ambient connection from another thread");
        FansOut("var work = Task.Factory.StartNew(Read);").Should().BeTrue();
        FansOut("var work = Task.Run<int>(Count);").Should().BeTrue(
            "an explicit type argument sits between the name and the parenthesis");
        FansOut("await Task.WhenAll<int>(first, second);").Should().BeTrue(
            "an explicit type argument sits between the name and the parenthesis");
        FansOut("var whenAllowed = policy.WhenAllowed;").Should().BeFalse();
        FansOut("await context.SaveChangesAsync(ct);").Should().BeFalse();
        FansOut("var rows = store.ReadAsync(ct); var more = other.ReadAsync(ct); await rows; await more;")
            .Should().BeTrue("two started tasks are Task.WhenAll written out longhand");
        FansOut("var one = context.Items.Where(x => x.Live).ToListAsync(ct);").Should().BeTrue(
            "a composed EF query is the shape this rule exists for, and a parenthesis in the "
            + "receiver used to end the name the pattern was looking for");
        FansOut("var rows = store.ReadAsync<int>(ct);").Should().BeTrue(
            "an explicit type argument ended it too");
        FansOut("var rows = await context.Items.Where(x => x.Live).ToListAsync(ct);").Should().BeFalse(
            "awaited where it is started, so nothing runs beside anything else");
        FansOut("var rows = await store.ReadAsync(ct);").Should().BeFalse(
            "awaiting at the call site is the shape the rule asks for");
    }

    /// <summary>
    /// Files allowed to fan out, by exact path under <c>backend/src</c>, each with why
    /// its concurrent work does not use the scoped ambient connection.
    /// </summary>
    private static readonly Dictionary<string, string> ParallelSites = new(StringComparer.Ordinal)
    {
        ["LearnStack.Infrastructure/Jobs/BackgroundJobControl.cs"] =
            "The process-owned independent renewal, recovery and terminal lanes retain their own privileged operations; "
            + "the exact prebound capacity checkpoint uses a fresh sequential scope on the original source owner, "
            + "never a control-lane ambient unit or module DbContext (ADR-0047/0059).",
        ["LearnStack.Infrastructure/Jobs/BackgroundJobCapacityCheckpointOperation.cs"] =
            "One prebound capacity checkpoint retains its actual task, private cancellation callbacks and source owner; "
            + "the exact executor enters a fresh sequential application scope after positive original cleanup, "
            + "never concurrently on an existing ambient unit (ADR-0047).",
        ["LearnStack.Infrastructure/Jobs/BackgroundJobTerminalNoStartOperation.Control.cs"] =
            "One prebound terminal-cleanup owner retains its actual privileged acquisition and sequential history probes; "
            + "each store operation owns an independent connection, while application cleanup enters its own sequential source "
            + "through the enumerated executor, never a control-lane ambient unit (ADR-0047).",
        ["LearnStack.Infrastructure/Jobs/BackgroundJobTerminalNoStartOperation.cs"] =
            "The prebound terminal cleanup owner publishes one actual app task with ExecutionContext flow suppressed; "
            + "its exact executor opens fresh sequential scopes on that owner and retains every read, write and callback "
            + "through physical cleanup, never borrowing an ambient connection (ADR-0047).",
        ["LearnStack.Infrastructure/Jobs/BackgroundJobRenewalOperation.cs"] =
            "One prebound renewal owner retains its actual privileged SQL task and a separate bounded wait; "
            + "both belong to the process lane, never an ambient unit or module DbContext (ADR-0047).",
        ["LearnStack.Infrastructure/Jobs/BackgroundJobExecutionControl.cs"] =
            "One generation-owned identity retains task, lease and cancellation observations through actual cleanup; "
            + "this helper acquires no connection, ambient unit or module DbContext (ADR-0047/0059).",
        ["LearnStack.Infrastructure/Jobs/BackgroundJobExecutionControl.External.cs"] =
            "One already retained external invocation publishes its actual sequential business task to both owners; "
            + "the executor alone opens a fresh private application scope, while provider execution holds no ambient unit "
            + "or module DbContext (ADR-0047/0061).",
        ["LearnStack.Infrastructure/Jobs/BackgroundJobRuntime.cs"] =
            "The process facade retains bootstrap, independent pool/shared/bridge owners and the fixed messaging/source lifetime; "
            + "all SQL is privileged or privately scoped, with no ambient unit or module DbContext (ADR-0047/0059/0060).",
        ["LearnStack.Infrastructure/Jobs/HangfireGeneration.cs"] =
            "One process-owned Core generation observes its startup, stop and actual joins independently; "
            + "stock operations use confined provider connections, never a scoped ambient unit (ADR-0059/0060).",
        ["LearnStack.Infrastructure/Jobs/HangfireSharedComponents.cs"] =
            "Two process-owned stock maintenance lanes retain actual native work and cancellation cleanup; "
            + "neither component holds an ambient unit or module DbContext (ADR-0058/0059).",
        ["LearnStack.Infrastructure/Jobs/HangfireGenerationWork.cs"] =
            "Generation-owned private cancellation observations retain callbacks through real cleanup; "
            + "the helper acquires no ambient unit, module DbContext or physical connection (ADR-0047/0059).",
        ["LearnStack.Infrastructure/Jobs/HangfireGenerationWork.StageWake.cs"] =
            "One invocation retains its single stage-clock observer before issuance with ExecutionContext flow suppressed; "
            + "the observer only signals fixed state and owns its timer through actual disposal, with no SQL, ambient unit "
            + "or module callback (ADR-0047/0059).",
        ["LearnStack.Infrastructure/Jobs/HangfireInvocationContract.cs"] =
            "The closed transport wrapper retains actual routing/admission tasks for generation cleanup and awaits them sequentially; "
            + "routing owns its privileged connection and JobExecutor creates fresh private scopes, never a wrapper ambient unit (ADR-0047/0059).",
        ["LearnStack.Infrastructure/Jobs/HangfirePoolOwner.cs"] =
            "One process-owned supervision loop retains each stock generation through actual cleanup and backoff; "
            + "it uses no ambient unit or module DbContext (ADR-0059).",
        ["LearnStack.Infrastructure/Jobs/BackgroundJobBridge.cs"] =
            "Process-owned bridge joins already-started stock client calls at shutdown; "
            + "each SQL quantum owns its own privileged connection and no ambient unit of work (ADR-0047).",
        ["LearnStack.Infrastructure/Caching/InMemoryCacheService.cs"] =
            "Singleton cache flights race their service-owned deadline and retain abandoned factories until termination; "
            + "they hold no scoped unit of work or module DbContext.",
        ["LearnStack.Infrastructure/MultiTenancy/CachedHostToTenantResolver.cs"] =
            "Singleton host lookup flights run before tenant admission on their own read-only connection/transaction "
            + "and hold no scoped unit of work or module DbContext (ADR-0036 and ADR-0040).",
    };

    /// <remarks>
    /// Two shapes. The named APIs, and the hand-rolled one: a call to an <c>…Async</c> method
    /// whose result is stored rather than awaited is a task already running, and two of those
    /// are `Task.WhenAll` written out longhand. Scoped module and shared execution code
    /// awaits at the call site; singleton flight exceptions are declared above.
    /// </remarks>
    private static bool FansOut(string code) =>
        FanOut().IsMatch(code) || StartedWithoutAwaiting().IsMatch(code);

    /// <summary>
    /// Concurrent execution: <c>Task.When*</c>, the <c>Parallel</c> loops, and PLINQ.
    /// </summary>
    /// <remarks>
    /// <c>Parallel.ForAsync</c> is named explicitly rather than left to a <c>For</c> prefix,
    /// and so is <c>AsParallel</c>: the first is the loop async module code reaches for, and
    /// the second runs a query on the thread pool without the word <c>Parallel</c> appearing
    /// where a reader expects it. An explicit type argument — <c>Task.WhenAll&lt;int&gt;(…)</c>
    /// — sits between the name and the parenthesis, so the pattern allows one.
    /// </remarks>
    [GeneratedRegex(
        @"\b(?:WhenAll|WhenAny|WhenEach)\s*(?:<[^;()<>]*>)?\s*\("
        + @"|\bParallel\s*\.\s*(?:ForAsync|ForEachAsync|ForEach|For|Invoke)\b"
        + @"|\bTask\s*\.\s*Run\s*(?:<[^;()<>]*>)?\s*\("
        + @"|\bTask\s*\.\s*Factory\s*\.\s*StartNew\s*(?:<[^;()<>]*>)?\s*\("
        + @"|\.\s*AsParallel\s*\(")]
    private static partial Regex FanOut();

    /// <summary>
    /// A task started and stored instead of awaited: <c>var rows = ReadAsync(...);</c>.
    /// </summary>
    /// <remarks>
    /// The initializer is any expression that reaches an <c>…Async(</c> call without an
    /// <c>await</c> before it, not a dotted name. Requiring <c>[\w.]*Async(</c> meant the
    /// ordinary shape this rule exists for — <c>context.Items.Where(x =&gt; x.Live)
    /// .ToListAsync(ct)</c> — did not match, because a parenthesis in the receiver ended the
    /// name; an explicit type argument ended it too. Both start an operation on the ambient
    /// connection before anything is awaited, which is the whole subject.
    /// </remarks>
    [GeneratedRegex(
        @"(?:^|[;{}])\s*(?:var|Task\b[^=;]*|ValueTask\b[^=;]*)\s+\w+\s*=\s*"
        + @"(?:(?!\bawait\b)[^;])*?\b\w*Async\s*(?:<[^;()<>]*>)?\s*\(",
        RegexOptions.Multiline)]
    private static partial Regex StartedWithoutAwaiting();

    /// <summary>
    /// Source with its comments removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every file the scan above touches argues in prose about the very call it
    /// is forbidden to make, so scanning raw text would fail on the documentation
    /// that explains the rule.
    /// </para>
    /// <para>
    /// Through the literal-aware <see cref="SourceText.WithoutComments"/>, not a
    /// regex. A regex has no literal state: the <c>//</c> inside
    /// <c>"https://…"</c> opens a line comment and deletes the rest of that line
    /// before the scan reads it, and a <c>/*</c> inside a string blanks an
    /// arbitrary multi-line region up to the next <c>*&#47;</c> anywhere in the
    /// file. This is the guard for the ADR-0040 seam; a hole in it is a hole in
    /// that.
    /// </para>
    /// </remarks>
    private static string StripComments(string source) => SourceText.WithoutComments(source);

    [Fact]
    public void The_registration_marker_does_not_vouch_across_containers()
    {
        // What a process-wide marker set got wrong. The rule's other leg —
        // Scoped + ImplementationFactory — cannot tell this helper's registration
        // from a hand-rolled scoped factory that builds the context on its own
        // connection string, which is precisely the ADR-0040 failure. So for that
        // one shape the marker is the whole guard, and a marker that answers for
        // the process rather than the container answers about a registration that
        // is not the one in front of it.
        var correct = new ServiceCollection();
        correct.AddModuleDbContext<ProbeDbContext>();
        correct.RegisteredContexts().Should().Contain(typeof(ProbeDbContext));

        var foreign = new ServiceCollection();
        foreign.AddScoped(_ => new ProbeDbContext(
            new DbContextOptionsBuilder<ProbeDbContext>().Options));

        foreign.RegisteredContexts().Should().BeEmpty(
            "the marker answers for the collection it was read from, not for "
            + "whatever any other container in this process registered first");
    }

    /// <summary>
    /// A unique index on a soft-deletable table counts only the rows that are not deleted.
    /// </summary>
    [Fact]
    public void Unique_Indexes_On_Soft_Deletable_Tables_Exclude_Deleted_Rows()
    {
        // A soft-deleted row keeps its natural key, so an index that counts it holds the key
        // against its own tenant forever — nothing frees a soft-deleted row. Every
        // soft-deletable table wrote its index partial on `deleted_at IS NULL` except one:
        // audit_config, whose override can only be changed by a soft delete and a fresh
        // Declare, which the unfiltered index refused with 23505 (the fifth review of
        // Packet 9). Per-table schema cases had pinned the others; nothing swept for the
        // shape, so a new table repeated the omission and every case stayed green.
        var contexts = Modules.Scoped.Select(module => module.Context()).ToList();

        try
        {
            var soft = contexts
                .SelectMany(context => context.Model.GetEntityTypes())
                .Where(IsSoftDeletable)
                .ToList();

            soft.Should().NotBeEmpty("a sweep over no soft-deletable table passes vacuously");

            var counting = UniqueIndexesCountingDeletedRows(soft);

            counting.Except(HeldByDecision.Keys).Should().BeEmpty(
                "a unique index on a soft-deletable table is partial on `deleted_at IS NULL`, "
                + "or the deleted row holds its key forever — unless holding it is the point, "
                + "which HeldByDecision states with its owner");

            HeldByDecision.Keys.Except(counting).Should().BeEmpty(
                "an exception whose index is gone or already partial is a stale exception");
        }
        finally
        {
            contexts.ForEach(context => context.Dispose());
        }
    }

    [Fact]
    public void The_Soft_Delete_Index_Sweep_Can_Actually_Fail()
    {
        // Every real index passes, so the rule passes whether its predicate works or not. The
        // probe carries one index of each shape: counting deleted rows, partial, one that
        // contains the primary key, and one on a table with no soft delete at all — only the
        // first is a finding.
        using var context = new SoftDeleteProbeContext();

        UniqueIndexesCountingDeletedRows(context.Model.GetEntityTypes())
            .Should().Equal("SoftProbe.ux_soft_probe_code");
    }

    /// <summary>
    /// Unique indexes that hold a soft-deleted row's key on purpose, each with why.
    /// </summary>
    private static readonly Dictionary<string, string> HeldByDecision = new(StringComparer.Ordinal)
    {
        // Packet 6's, and not an omission: a tenant's slug is its public hostname label,
        // `{slug}.{platform-domain}`. Releasing it would hand a later tenant the links, mail
        // and bookmarks that still point at the first one — a subdomain takeover by
        // deletion. Whether a terminated tenant's slug may ever be reissued is decided
        // with tenant termination, in Phase 02c, not by an index filter.
        ["Tenant.ux_tenants_slug"] =
            "a tenant slug is a hostname; its reissue is Phase 02c's decision",
    };

    private static bool IsSoftDeletable(IReadOnlyEntityType entity) =>
        entity.GetProperties().Any(property =>
            string.Equals(property.GetColumnName(), "deleted_at", StringComparison.Ordinal));

    /// <summary>The unique indexes on soft-deletable tables that do not exclude deleted rows.</summary>
    /// <remarks>
    /// An index that contains the whole primary key is outside the rule: it is unique by
    /// construction, so a deleted row holds nothing a live one could want — and it is how a
    /// composite foreign key reaches the table (<c>ux_organizations_tenant_id_id</c>), which a
    /// partial index cannot serve.
    /// </remarks>
    private static List<string> UniqueIndexesCountingDeletedRows(IEnumerable<IReadOnlyEntityType> entities) =>
        [.. entities
            .Where(IsSoftDeletable)
            .SelectMany(entity => entity.GetIndexes()
                .Where(index => index.IsUnique
                    && !ContainsPrimaryKey(entity, index)
                    && !(index.GetFilter() ?? string.Empty)
                        .Contains("deleted_at IS NULL", StringComparison.OrdinalIgnoreCase))
                .Select(index => $"{entity.ClrType.Name}.{index.GetDatabaseName()}"))
            .Order(StringComparer.Ordinal)];

    private static bool ContainsPrimaryKey(IReadOnlyEntityType entity, IReadOnlyIndex index) =>
        entity.FindPrimaryKey() is { } key && key.Properties.All(index.Properties.Contains);

    private sealed class SoftProbe
    {
        public int Id { get; set; }

        public string Code { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public DateTimeOffset? DeletedAt { get; set; }
    }

    private sealed class HardProbe
    {
        public int Id { get; set; }

        public string Code { get; set; } = string.Empty;
    }

    private sealed class SoftDeleteProbeContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseNpgsql("Host=model-only;Database=model-only;Username=model-only");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SoftProbe>(builder =>
            {
                builder.Property(x => x.DeletedAt).HasColumnName("deleted_at");
                builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_soft_probe_code");
                builder.HasIndex(x => x.Name).IsUnique().HasFilter("deleted_at IS NULL")
                    .HasDatabaseName("ux_soft_probe_name");
                builder.HasIndex(x => new { x.Code, x.Id }).IsUnique()
                    .HasDatabaseName("ux_soft_probe_code_id");
            });

            modelBuilder.Entity<HardProbe>(builder =>
                builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_hard_probe_code"));
        }
    }

    /// <summary>A context type that exists only to be registered two ways.</summary>
    private sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options)
        : DbContext(options);

    [Fact]
    public void Every_Database_Test_Carries_The_Docker_Trait()
    {
        // CI splits the integration assembly by `[Trait("Requires","Docker")]`, and
        // the two filters are exact complements — so a class that forgets the
        // attribute does not fail, it runs in the `backend` job. Both jobs are on
        // ubuntu-latest, which carries a Docker socket natively, so it starts its
        // container and passes: nothing goes red, and the Docker suite quietly
        // stops being where the Docker tests live.
        //
        // The whole project, not only Database/: a test beside the HTTP suites can take
        // the shared schema too — ApiHandlerCompositionTests does — and a sweep of one
        // folder could not see it forget the trait (the fifth review of Packet 9).
        //
        // Source-scanned rather than reflected, because this assembly does not
        // reference LearnStack.Tests.Integration — and should not: a test project
        // referencing another test project is a dependency nothing else in the
        // repository has.
        var project = Path.Combine(
            RepositoryPaths.RepoRoot(), "backend", "tests", "LearnStack.Tests.Integration");

        var files = Directory
            .EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(file => (Path.GetRelativePath(project, file).Replace('\\', '/'), File.ReadAllText(file)))
            .ToList();

        files.Should().Contain(file => file.Item1.StartsWith("Database/", StringComparison.Ordinal),
            "the Docker-bound suite lives there, and a rule that scans nothing passes");

        UntraitedDockerTests(files).Should().BeEmpty(
            "every test class under Database/, and every one that takes a database fixture, "
            + "needs a real Docker socket, and the trait is how CI routes it to the job that "
            + "declares one");

        NativePartitionOffenders(files).Should().BeEmpty(
            "ADR-0062 assigns both complete native proof classes, including their predicate and mutant rows, "
            + "to the native VM; a new direct or delegated proof cannot silently enter ordinary Docker");
    }

    [Fact]
    public void The_Docker_Trait_Sweep_Can_Actually_Fail()
    {
        // Every file in the repository carries the trait it needs, so the sweep above
        // passes on a project where it is doing its job and on one where it is not. These
        // are the four shapes it has to tell apart.
        const string Trait = "[Trait(RequiresDocker.Key, RequiresDocker.Value)]";
        const string Qualified = "[Trait(Database.RequiresDocker.Key, Database.RequiresDocker.Value)]";

        UntraitedDockerTests(
        [
            ("Database/ForgotTests.cs", "public class ForgotTests { [Fact] public void A() { } }"),
            ("Database/TraitedTests.cs", $"{Trait} public class TraitedTests {{ [Fact] public void A() {{ }} }}"),
            ("Database/SchemaQueries.cs", "public static class SchemaQueries { }"),
            ("RootForgotTests.cs", "public class RootForgotTests(Database.SchemaFixture schema) { [Fact] public void A() { } }"),
            ("RootTraitedTests.cs", $"{Qualified} public class RootTraitedTests(Database.SchemaFixture schema) {{ [Theory] public void A() {{ }} }}"),
            ("HttpOnlyTests.cs", "public class HttpOnlyTests { [Fact] public void A() { } }"),
            ("Database/PartialMain.cs", $"namespace Valid; {Trait} public partial class SplitTests {{ }}"),
            ("Database/PartialCase.cs", "namespace Valid; public partial class SplitTests { [Fact] public void A() { } }"),
            ("Database/MissingPartialMain.cs", "namespace Missing; public partial class SplitTests { }"),
            ("Database/MissingPartialCase.cs", "namespace Missing; public partial class SplitTests { [Fact] public void A() { } }"),
            ("Database/OtherNamespaceTrait.cs", $"namespace Other; {Trait} public partial class SplitTests {{ }}"),
            ("Database/OtherClassTrait.cs", $"namespace Missing; {Trait} public partial class DifferentTests {{ }}"),
            ("Database/SameFile.cs", $"{Trait} public class CleanTests {{ [Fact] public void A() {{ }} }} "
                + "public class UntraitedTests { [Fact] public void B() { } }"),
            ("Database/CommentOnly.cs", $"// {Trait}\npublic class CommentOnlyTests {{ [Fact] public void A() {{ }} }}"),
            ("Database/ForeignTrait.cs", "[Other.Trait(RequiresDocker.Key, RequiresDocker.Value)] public class ForeignTraitTests { [Fact] public void A() { } }"),
            ("Database/NonPartialTrait.cs", $"namespace Unmerged; {Trait} public class UnmergedTests {{ }}"),
            ("Database/NonPartialCase.cs", "namespace Unmerged; public class UnmergedTests { [Fact] public void A() { } }"),
        ]).Should().Equal("Database/CommentOnly.cs", "Database/ForeignTrait.cs", "Database/ForgotTests.cs", "Database/MissingPartialCase.cs",
            "Database/NonPartialCase.cs", "Database/SameFile.cs", "RootForgotTests.cs");

        const string Native = "[Trait(NativeCrashProof.Key, NativeCrashProof.Value)]";
        const string Namespace = "namespace LearnStack.Tests.Integration.Database; ";
        var nativeFiles = new (string Path, string Source)[]
        {
            ("Database/Fatal.cs", Namespace + Trait + Native
                + " public class HangfireFatalProcessTests { [Fact] public void A() { Probe.Run(); } }"),
            ("Database/Phase.cs", Namespace + Qualified
                + "[global::Xunit.TraitAttribute(Database.NativeCrashProof.Key, Database.NativeCrashProof.Value)]"
                + " public partial class HangfireGenerationPhaseTests { }"),
            ("Database/Phase.Cases.cs", Namespace
                + "public partial class HangfireGenerationPhaseTests { [Theory] [InlineData(1)] public void A(int value) { HangfireGenerationPhaseHarness.Run(); } }"),
            ("Database/Harness.cs", Namespace
                + "internal static class HangfireGenerationPhaseHarness { public static void Run() { Probe.Run(); } }"
                + " internal static class Probe { public static void Run() { _ = \"preflight.sh\"; } }"),
        };
        NativePartitionOffenders(nativeFiles).Should().BeEmpty();

        void RejectNative(string diagnostic, params (string Path, string Source)[] added) =>
            NativePartitionOffenders(nativeFiles.Concat(added)).Should().Contain(diagnostic);

        void RejectChangedNative(string before, string after, string diagnostic) =>
            NativePartitionOffenders(nativeFiles.Select(file =>
                (file.Path, file.Source.Replace(before, after, StringComparison.Ordinal))))
                .Should().Contain(diagnostic);

        const string Fatal = "LearnStack.Tests.Integration.Database.HangfireFatalProcessTests`0";
        const string Phase = "LearnStack.Tests.Integration.Database.HangfireGenerationPhaseTests`0";
        RejectChangedNative(Native, "", "Native trait missing: " + Fatal);
        RejectChangedNative(Native, "[Trait(NativeCrashProof.Key, \"false\")]", "Native trait missing: " + Fatal);
        RejectChangedNative(Native, "[Other.Trait(NativeCrashProof.Key, NativeCrashProof.Value)]", "Native trait missing: " + Fatal);
        RejectChangedNative(Native, "/* " + Native + " */", "Native trait missing: " + Fatal);
        RejectChangedNative(Trait, "", "Native Docker trait missing: " + Fatal);
        RejectChangedNative(Native, "[Trait(\"NativeCrashProof\", \"true\")]", "Native trait missing: " + Fatal);
        RejectChangedNative(Native, Native + "[Trait(NativeCrashProof.Key, \"false\")]",
            "Native trait must use shared constants: " + Fatal);
        RejectChangedNative("public partial class HangfireGenerationPhaseTests", "public class HangfireGenerationPhaseTests",
            "Native trait missing: " + Phase);
        RejectChangedNative("[Fact] public void A()", Native + "[Fact] public void A()",
            "Method native trait: " + Fatal);
        RejectChangedNative("namespace LearnStack.Tests.Integration.Database; [Trait(Database.RequiresDocker.Key",
            "namespace Other; [Trait(Database.RequiresDocker.Key", "Native trait missing: " + Phase);
        RejectChangedNative("_ = \"preflight.sh\";", "", "Native launch census is empty");
        NativePartitionOffenders([]).Should().Contain("Native source census is empty");
        NativePartitionOffenders(nativeFiles.Where(file => file.Path != "Database/Fatal.cs"))
            .Should().Contain("Native test class missing or duplicated: " + Fatal);
        NativePartitionOffenders(nativeFiles.Where(file => file.Path is not ("Database/Fatal.cs" or "Database/Phase.Cases.cs")))
            .Should().Contain("Native test census is empty");

        RejectNative("Unexpected native trait: Other.ThirdTests`0",
            ("Third.cs", "namespace Other; " + Native + " public class ThirdTests { [Fact] public void A() { } }"));
        RejectNative("Unclassified native test: Other.ThirdTests`0",
            ("Third.cs", "namespace Other; public class ThirdTests { [Fact] public void A() { LearnStack.Tests.Integration.Database.HangfireGenerationPhaseHarness.Run(); } }"));
        RejectNative("Unclassified native test: Other.ThirdTests`0",
            ("Third.cs", "namespace Other; public class ThirdTests { [Fact] public void A() { First.Run(); } }"),
            ("First.cs", "namespace Other; internal static class First { public static void Run() { Second.Run(); } }"),
            ("Second.cs", "namespace Other; internal static class Second { public static void Run() { LearnStack.Tests.Integration.Database.HangfireGenerationPhaseHarness.Run(); } }"));
        RejectNative("Unclassified native test: Other.ThirdTests`0",
            ("Third.cs", "namespace Other; public partial class ThirdTests { [Fact] public void A() { Run(); } }"),
            ("Third.Helpers.cs", "namespace Other; public partial class ThirdTests { private static void Run() { LearnStack.Tests.Integration.Database.HangfireGenerationPhaseHarness.Run(); } }"));
        RejectNative("Unclassified native test: Other.ThirdTests`0",
            ("Third.cs", "using Harness = LearnStack.Tests.Integration.Database.HangfireGenerationPhaseHarness; namespace Other; public class ThirdTests { [Fact] public void A() { Harness.Run(); } }"));
        RejectNative("Unclassified native test: Other.ThirdTests`0",
            ("Third.cs", "using static LearnStack.Tests.Integration.Database.HangfireGenerationPhaseHarness; namespace Other; public class ThirdTests { [Fact] public void A() { Run(); } }"));
        RejectNative("Unclassified native test: Other.ThirdTests`0",
            ("Third.cs", "namespace Other; public class ThirdTests { [Fact] public void A() { Copied.Run(); } }"),
            ("Copied.cs", "namespace Other; internal static class Copied { public static void Run() { _ = \"pre\" + \"flight.sh\"; } }"));
        RejectNative("Unclassified native test: Other.ThirdTests`0",
            ("Third.cs", "namespace Other; public class ThirdTests { [Fact] public void A() { Copied.Run(); } }"),
            ("Copied.cs", "namespace Other; internal static class Copied { public static void Run() { _ = Paths.Script; } } internal static class Paths { public const string Script = \"job-lifecycle-probe\"; }"));
        RejectNative("Unclassified native test: Other.Outer`0.ThirdTests`1",
            ("Third.cs", "namespace Other; public class Outer { public class ThirdTests<T> { [Fact] public void A() { LearnStack.Tests.Integration.Database.HangfireGenerationPhaseHarness.Run(); } } }"));
        RejectNative("Unexpected native trait: Other.HangfireFatalProcessTests`0",
            ("Third.cs", "namespace Other; " + Native + " public class HangfireFatalProcessTests { [Fact] public void A() { } }"));
        RejectNative("Unclassified native test: Other.ThirdTests`0",
            ("Third.cs", "using First; using Second; namespace Other; public class ThirdTests { [Fact] public void A() { Ambiguous.Run(); } }"),
            ("First.cs", "namespace First; internal static class Ambiguous { public static void Run() { LearnStack.Tests.Integration.Database.HangfireGenerationPhaseHarness.Run(); } }"),
            ("Second.cs", "namespace Second; internal static class Ambiguous { public static void Run() { } }"));

        // Binding must distinguish a same-named unrelated type and a local variable
        // from the real harness. Neither spelling alone grants native ownership.
        NativePartitionOffenders(nativeFiles.Concat(new (string Path, string Source)[]
        {
            ("Other.cs", "namespace Other; internal static class HangfireGenerationPhaseHarness { public static void Run() { } } public class ThirdTests { [Fact] public void A() { HangfireGenerationPhaseHarness.Run(); } }"),
            ("Shadow.cs", "namespace Shadow; public class ThirdTests { [Fact] public void A() { var HangfireGenerationPhaseHarness = 1; _ = HangfireGenerationPhaseHarness; } }"),
        })).Should().BeEmpty();
    }

    private static readonly string[] NativeProofClasses =
    [
        "LearnStack.Tests.Integration.Database.HangfireFatalProcessTests`0",
        "LearnStack.Tests.Integration.Database.HangfireGenerationPhaseTests`0",
    ];

    private static List<string> NativePartitionOffenders(IEnumerable<(string Path, string Source)> files)
    {
        // One bounded source compilation per population, without emission, test-project
        // references or loading the fatal sidecar. Only declared type dependencies are
        // consumed; this is not a claim that arbitrary test bodies compile in isolation.
        var trees = files.Select(file => CSharpSyntaxTree.ParseText(file.Source,
            new CSharpParseOptions(LanguageVersion.Latest), file.Path)).ToArray();
        if (trees.Length == 0) return ["Native source census is empty"];
        var compilation = CSharpCompilation.Create("NativePartition", trees,
            new[] { typeof(object).Assembly.Location, typeof(FactAttribute).Assembly.Location }
                .Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var declarations = trees.SelectMany(tree =>
        {
            var model = compilation.GetSemanticModel(tree);
            return tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>()
                .Select(type => new NativeTestDeclaration(TypeIdentity(type), type, model));
        }).ToArray();
        var byName = declarations.GroupBy(part => part.Syntax.Identifier.ValueText, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(part => part.Identity)
                .ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var aliases = trees.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>())
            .Where(import => import.Alias is not null).Select(import => import.Alias!.Name.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);
        var parts = declarations.GroupBy(part => part.Identity, StringComparer.Ordinal)
            .SelectMany(group => group.Count() == 1 || group.All(part => part.Syntax.Modifiers.Any(SyntaxKind.PartialKeyword))
                ? new[] { group.ToArray() } : group.Select(part => new[] { part }))
            .ToArray();
        var dependencies = parts.ToLookup(group => group[0].Identity,
            group => group.SelectMany(part => NativeTypeDependencies(part, byName, aliases))
                .ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var launchParts = declarations.Where(part => part.Syntax.DescendantNodes().OfType<ExpressionSyntax>()
            .Select(NativeConstantString).Any(value => value is not null
                && (value.Contains("preflight.sh", StringComparison.Ordinal)
                    || value.Contains("job-lifecycle-probe", StringComparison.Ordinal)
                    || value.Contains("LearnStack.Tests.JobLifecycleProbe", StringComparison.Ordinal))))
            .ToArray();
        var native = launchParts.Select(part => part.Identity).Concat(declarations
            .Where(part => part.Identity is "LearnStack.Tests.Integration.Database.HangfireGenerationPhaseHarness`0"
                or "LearnStack.Tests.Integration.Database.HangfireGenerationPhaseMutation`0")
            .Select(part => part.Identity)).ToHashSet(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var group in dependencies)
                if (!native.Contains(group.Key) && group.Any(edges => edges.Overlaps(native)))
                    changed |= native.Add(group.Key);
        } while (changed);

        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        if (launchParts.Length == 0) offenders.Add("Native launch census is empty");
        var testParts = parts.Where(group => group.Any(part => part.Syntax.Members.OfType<MethodDeclarationSyntax>()
            .Any(method => method.AttributeLists.SelectMany(list => list.Attributes)
                .Any(attribute => AttributeName(attribute) is "Fact" or "Theory")))).ToArray();
        if (testParts.Length == 0) offenders.Add("Native test census is empty");
        foreach (var expected in NativeProofClasses)
            if (testParts.Count(group => group[0].Identity == expected) != 1)
                offenders.Add("Native test class missing or duplicated: " + expected);
        foreach (var group in parts)
        {
            var identity = group[0].Identity;
            var attributes = group.SelectMany(part => part.Syntax.AttributeLists.SelectMany(list => list.Attributes)).ToArray();
            var approved = NativeProofClasses.Contains(identity, StringComparer.Ordinal);
            if (!approved && attributes.Any(IsNativeTraitKey)) offenders.Add("Unexpected native trait: " + identity);
            if (group.SelectMany(part => part.Syntax.Members.OfType<MethodDeclarationSyntax>())
                .SelectMany(method => method.AttributeLists.SelectMany(list => list.Attributes)).Any(IsNativeTraitKey))
                offenders.Add("Method native trait: " + identity);
            if (!testParts.Contains(group)) continue;
            if (native.Contains(identity) && !approved) offenders.Add("Unclassified native test: " + identity);
            if (!approved) continue;
            if (!attributes.Any(IsNativeTrait)) offenders.Add("Native trait missing: " + identity);
            if (attributes.Any(attribute => IsNativeTraitKey(attribute) && !IsNativeTrait(attribute)))
                offenders.Add("Native trait must use shared constants: " + identity);
            if (!attributes.Any(IsDockerTrait)) offenders.Add("Native Docker trait missing: " + identity);
        }
        return offenders.ToList();
    }

    private sealed record NativeTestDeclaration(string Identity, TypeDeclarationSyntax Syntax, SemanticModel Model);

    private static IEnumerable<string> NativeTypeDependencies(NativeTestDeclaration part,
        Dictionary<string, HashSet<string>> byName, HashSet<string> aliases)
    {
        var imports = part.Syntax.SyntaxTree.GetCompilationUnitRoot().Usings.Concat(part.Syntax.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>().SelectMany(scope => scope.Usings));
        var names = part.Syntax.DescendantNodes().OfType<SimpleNameSyntax>()
            .Where(name => byName.ContainsKey(name.Identifier.ValueText) || aliases.Contains(name.Identifier.ValueText))
            .Cast<SyntaxNode>().Concat(imports.Where(import => import.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
                .Select(import => import.Name).OfType<SyntaxNode>());
        foreach (var name in names)
        {
            var symbol = part.Model.GetSymbolInfo(name).Symbol;
            if (name is IdentifierNameSyntax identifier)
                symbol = part.Model.GetAliasInfo(identifier)?.Target ?? symbol;
            var type = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
            if (type is not null)
            {
                foreach (var reference in type.OriginalDefinition.DeclaringSyntaxReferences)
                    if (reference.GetSyntax() is TypeDeclarationSyntax declared) yield return TypeIdentity(declared);
            }
            else if (symbol is null && name is SimpleNameSyntax unresolved
                && byName.TryGetValue(unresolved.Identifier.ValueText, out var candidates))
            {
                // Missing external references do not authorize silently dropping a
                // possible source helper. Ambiguity conservatively taints all candidates.
                foreach (var candidate in candidates) yield return candidate;
            }
        }
    }

    private static string? NativeConstantString(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
        ParenthesizedExpressionSyntax parenthesized => NativeConstantString(parenthesized.Expression),
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression)
            && NativeConstantString(binary.Left) is { } left && NativeConstantString(binary.Right) is { } right => left + right,
        _ => null,
    };

    private static bool IsNativeTraitKey(AttributeSyntax attribute) => IsXunitTrait(attribute)
        && attribute.ArgumentList is { Arguments.Count: 2 } arguments
        && (NativeConstantString(arguments.Arguments[0].Expression) == "NativeCrashProof"
            || arguments.Arguments[0].Expression.DescendantTokens().Any(token => token.ValueText == "NativeCrashProof"));

    private static bool IsNativeTrait(AttributeSyntax attribute)
    {
        if (!IsXunitTrait(attribute) || attribute.ArgumentList is not { Arguments.Count: 2 } arguments) return false;
        var key = string.Concat(arguments.Arguments[0].Expression.DescendantTokens().Select(token => token.Text))
            .Replace("global::", "", StringComparison.Ordinal);
        var value = string.Concat(arguments.Arguments[1].Expression.DescendantTokens().Select(token => token.Text))
            .Replace("global::", "", StringComparison.Ordinal);
        return (key, value) is ("NativeCrashProof.Key", "NativeCrashProof.Value")
            or ("Database.NativeCrashProof.Key", "Database.NativeCrashProof.Value")
            or ("LearnStack.Tests.Integration.Database.NativeCrashProof.Key", "LearnStack.Tests.Integration.Database.NativeCrashProof.Value");
    }

    private static bool IsXunitTrait(AttributeSyntax attribute) =>
        string.Concat(attribute.Name.DescendantTokens().Select(token => token.Text))
            is "Trait" or "TraitAttribute" or "Xunit.Trait" or "Xunit.TraitAttribute"
            or "global::Xunit.Trait" or "global::Xunit.TraitAttribute";

    /// <summary>
    /// The test files that need Docker and do not say so: under <c>Database/</c>, or naming
    /// a fixture that starts a container, and carrying no <c>RequiresDocker</c> trait.
    /// </summary>
    private static List<string> UntraitedDockerTests(IEnumerable<(string Path, string Source)> files)
    {
        var declarations = files.SelectMany(file => CSharpSyntaxTree.ParseText(file.Source).GetRoot()
            .DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Select(type => new DockerTestDeclaration(file.Path, TypeIdentity(type),
                type.Modifiers.Any(token => token.RawKind == (int)SyntaxKind.PartialKeyword),
                type.Members.OfType<MethodDeclarationSyntax>().Any(method => method.AttributeLists
                    .SelectMany(list => list.Attributes).Any(attribute => AttributeName(attribute) is "Fact" or "Theory")),
                file.Path.StartsWith("Database/", StringComparison.Ordinal)
                    || type.DescendantNodes().OfType<IdentifierNameSyntax>()
                        .Any(name => DockerFixtures.Contains(name.Identifier.ValueText)),
                type.AttributeLists.SelectMany(list => list.Attributes).Any(IsDockerTrait))))
            .ToArray();

        return declarations.GroupBy(type => type.Identity, StringComparer.Ordinal)
            .SelectMany(group =>
            {
                var parts = group.ToArray();
                var isPartialType = parts.All(part => part.Partial);
                return parts.Where(part => part.DeclaresTests
                    && (part.NeedsDocker || isPartialType && parts.Any(other => other.NeedsDocker))
                    && !(part.HasDockerTrait || isPartialType && parts.Any(other => other.HasDockerTrait)));
            })
            .Select(type => type.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    private sealed record DockerTestDeclaration(string Path, string Identity, bool Partial,
        bool DeclaresTests, bool NeedsDocker, bool HasDockerTrait);

    private static string TypeIdentity(TypeDeclarationSyntax declaration)
    {
        var namespaces = declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse().Select(scope => scope.Name.ToString());
        var types = declaration.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Reverse()
            .Select(type => type.Identifier.ValueText + "`" + (type.TypeParameterList?.Parameters.Count ?? 0));
        return string.Join(".", namespaces.Concat(types));
    }

    private static string AttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name.ToString().Replace("global::", "", StringComparison.Ordinal).Split('.')[^1];
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    private static bool IsDockerTrait(AttributeSyntax attribute)
    {
        var name = string.Concat(attribute.Name.DescendantTokens().Select(token => token.Text));
        if (name is not ("Trait" or "TraitAttribute" or "Xunit.Trait" or "Xunit.TraitAttribute"
            or "global::Xunit.Trait" or "global::Xunit.TraitAttribute") || attribute.ArgumentList?.Arguments.Count != 2)
            return false;
        var key = string.Concat(attribute.ArgumentList.Arguments[0].Expression.DescendantTokens().Select(token => token.Text));
        var value = string.Concat(attribute.ArgumentList.Arguments[1].Expression.DescendantTokens().Select(token => token.Text));
        return (key, value) is ("RequiresDocker.Key", "RequiresDocker.Value")
            or ("Database.RequiresDocker.Key", "Database.RequiresDocker.Value");
    }

    /// <summary>The fixtures that start a PostgreSQL container, by the names a test takes them under.</summary>
    private static readonly string[] DockerFixtures = ["SchemaFixture", "PostgresFixture", "SharedSchema"];

    [Fact]
    public void Migrate_Target_Covers_Every_Migration_Chain()
    {
        // `make migrate` is the only documented path that applies a migration, and
        // its project loop is a glob. The first version globbed `src/Modules` only,
        // which left the platform chain — outbox_messages and idempotency_keys —
        // unapplied by every documented path while the Testcontainers fixtures,
        // which call Database.MigrateAsync() directly, stayed green.
        //
        // Scanned rather than listed: the assertion is that every directory under
        // backend/src carrying a Persistence/Migrations folder is reachable from
        // the recipe, so adding a chain and forgetting the Makefile fails here
        // rather than in a deployment.
        var recipe = ReadMigrateRecipe();

        var chains = MigrationChains();

        chains.Should().NotBeEmpty("the tenancy and platform chains both exist");

        var uncovered = chains
            .Where(chain => !RecipeReaches(recipe, chain))
            .ToList();

        uncovered.Should().BeEmpty(
            "`make migrate` applies every chain, or the ones it misses are "
            + "unmigrated on the only path Standards 05 § Database roles documents");
    }

    /// <summary>
    /// <c>make migrate</c> applies Tenancy before the Audit and Education chains.
    /// </summary>
    [Fact]
    public void Migrate_Target_Applies_The_Tenancy_Chain_First()
    {
        // From Phase 02a Packet 9 the chains are no longer independent: audit_config
        // carries the schema's only foreign key crossing two chains, to `tenants`,
        // which the Tenancy chain creates (ADR-0044 § 9). A run that reaches the Audit
        // chain first fails on a clean database with `relation "tenants" does not
        // exist` — and alphabetical order produces exactly that run, because
        // `Modules/Audit` sorts before `Modules/Tenancy` and the recipe's project list
        // is a glob.
        //
        // Coverage is not order, and this rule exists because the difference is
        // invisible on a database that already has the schema.
        // Migrate_Target_Covers_Every_Migration_Chain stays green when the Tenancy
        // prefix is deleted from the recipe — the glob still reaches Tenancy — while
        // every fresh deployment breaks from that commit onward. So does every suite
        // whose fixture applies the chains in its own order.
        var order = MigrateChainOrder(ReadMigrateRecipe());
        MigrationDependencyOffenders(order).Should().BeEmpty(
            "Fix: apply Tenancy before Audit (tenants FK) and Education "
            + "(the shared organization immutability function), per Standards 05 § Migrations");
    }

    [Theory]
    [InlineData("Audit")]
    [InlineData("Education")]
    public void The_Migration_Order_Guard_Rejects_Each_Dependent_Chain_First(string module)
    {
        var dependent = module == "Audit" ? AuditChain : EducationChain;
        var glob = "backend/src/Modules/*/LearnStack.Modules.*.Infrastructure";
        var bad = MigrateChainOrder($"{dependent} {TenancyChain} {glob}");
        MigrationDependencyOffenders(bad).Should().ContainSingle().Which.Should().Be(dependent);
        MigrationDependencyOffenders(MigrateChainOrder($"{TenancyChain} {glob}"))
            .Should().BeEmpty("the same glob expansion and detector must accept the repaired order");
    }

    private static List<string> MigrationDependencyOffenders(List<string> order) =>
        new[] { AuditChain, EducationChain }
            .Where(chain => !order.Contains(TenancyChain) || !order.Contains(chain)
                || order.IndexOf(TenancyChain) >= order.IndexOf(chain))
            .ToList();

    private const string TenancyChain =
        "backend/src/Modules/Tenancy/LearnStack.Modules.Tenancy.Infrastructure";

    private const string AuditChain =
        "backend/src/Modules/Audit/LearnStack.Modules.Audit.Infrastructure";

    private const string EducationChain =
        "backend/src/Modules/Education/LearnStack.Modules.Education.Infrastructure";

    /// <summary>
    /// The chains <c>make migrate</c> visits, in the order it visits them.
    /// </summary>
    /// <remarks>
    /// A faithful replay of the recipe rather than a reading of it: each
    /// <c>backend/src</c> token is expanded against the chains that actually exist —
    /// ordinal-sorted, which is what a shell does with a glob — and a chain already
    /// visited is skipped, which is what the recipe's <c>applied</c> guard does.
    /// <b>Expanding the glob is the whole difference from a text search</b>, and it is
    /// what catches the mutation that actually happened: deleting the explicit Tenancy
    /// prefix leaves a recipe whose only token is the <c>Modules/*</c> glob, in which the
    /// literal <c>Modules/Tenancy</c> does not appear at all — so a search for it finds
    /// nothing to compare, while the replay expands the glob and reports Audit first.
    /// </remarks>
    private static List<string> MigrateChainOrder(string recipe)
    {
        var chains = MigrationChains();
        var visited = new List<string>();

        var tokens = recipe
            .Split([' ', '\n', '\t', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.StartsWith("backend/src", StringComparison.Ordinal));

        foreach (var token in tokens)
        {
            var pattern = "^" + string.Join(
                "[^/]*",
                token.Split('*').Select(System.Text.RegularExpressions.Regex.Escape)) + "$";

            foreach (var chain in chains.Where(chain =>
                System.Text.RegularExpressions.Regex.IsMatch(chain, pattern)))
            {
                if (!visited.Contains(chain, StringComparer.Ordinal))
                {
                    visited.Add(chain);
                }
            }
        }

        return visited;
    }

    /// <summary>Every project under <c>backend/src</c> carrying a migration chain.</summary>
    private static List<string> MigrationChains() =>
        Directory
            .EnumerateDirectories(RepositoryPaths.BackendSrc(), "Migrations", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(Path.GetDirectoryName(path)) == "Persistence")
            .Select(path => Path.GetDirectoryName(Path.GetDirectoryName(path))!)
            .Select(project => Path.GetRelativePath(RepositoryPaths.RepoRoot(), project)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    [Theory]
    // Npgsql parses every one of these into Username / Password — measured
    // against Npgsql 10, not read off a page. The recipe recognised the first
    // pair only, so the other three read the role as empty and printed the
    // password unredacted.
    [InlineData("Username", "Password")]
    [InlineData("UID", "PWD")]
    [InlineData("User ID", "PSW")]
    [InlineData("USERID", "Pwd")]
    public void Migrate_Target_Refuses_An_Aliased_Runtime_Credential(string userKey, string secretKey)
    {
        // Executed, not scanned. A test that asserted the recipe's text contained
        // "uid" would pass against a recipe that matched the alias and then did
        // nothing with it — and the defect being fixed here is precisely a keyword
        // table that existed and was incomplete.
        var value = $"Host=localhost;Database=learnstack;{userKey}=learnstack_app;{secretKey}=hunter2"; // leakwatch:ignore

        var (exitCode, output) = RunMigrateTarget(value);

        exitCode.Should().NotBe(0, "learnstack_app does not own the tables: {0}", output);
        output.Should().Contain("learnstack_app", "the operator has to be told which role they named");
        output.Should().NotContain(
            "hunter2",
            "the one target whose purpose is keeping the migration credential in one "
            + "place must not echo it into a terminal or a CI log");
    }

    [Theory]
    // Npgsql accepts a semicolon inside a quoted value — measured against
    // Npgsql 10, both quote characters, with a doubled quote as the escape. The
    // split-on-";" version cut such a value in half: the first half matched the
    // keyword table and was redacted, the second half matched nothing and was
    // printed, so `make migrate` echoed a "redacted" string that still carried
    // the password.
    //
    // Every literal is an INPUT to the redaction under test — the file cannot be
    // written without one. They name localhost and a password no service has
    // ever had; leakwatch:ignore applies per line.
    [InlineData("Host=localhost;Password=\";hunter2\";UID=learnstack_app")] // leakwatch:ignore
    [InlineData("Host=localhost;Password=';hunter2';UID=learnstack_app")] // leakwatch:ignore
    [InlineData("Host=localhost;PWD=\"a;hunter2\";Username=learnstack_app")] // leakwatch:ignore
    [InlineData("Host=localhost;Password=\"said \"\"hi\"\";hunter2\";Username=learnstack_app")] // leakwatch:ignore
    public void Migrate_Target_Redacts_A_Quoted_Value_Whole(string value)
    {
        var (exitCode, output) = RunMigrateTarget(value);

        exitCode.Should().NotBe(0, "{0}", output);
        output.Should().Contain("learnstack_app", "the role is still read correctly");
        output.Should().NotContain(
            "hunter2",
            "a semicolon inside a quoted value is not a field boundary, so the "
            + "redaction covers the value whole or it covers nothing");
    }

    [Fact]
    public void Migrate_Target_Reads_The_Role_Through_A_Quoted_Value()
    {
        // The other half: a quoted password containing a semicolon must not
        // shift the fields that follow it out of alignment, or the role check
        // reads the wrong token and the recipe refuses a correct credential.
        var (exitCode, output) = RunMigrateTarget(
            "Host=localhost;Password=\";hunter2\";Username=learnstack_app"); // leakwatch:ignore

        exitCode.Should().NotBe(0, "{0}", output);
        output.Should().Contain(
            "Username='learnstack_app'",
            "the field after the quoted value is still parsed as its own field");
    }

    [Fact]
    public void Migrate_Target_Refuses_A_Uri_Without_Echoing_Its_Userinfo()
    {
        // The form DATABASE_URL carries on several hosts, and the form that has no
        // `password=` in it for a keyword pass to find.
        var (exitCode, output) = RunMigrateTarget(
            "postgres://learnstack_app:hunter2@localhost:5432/learnstack"); // leakwatch:ignore

        exitCode.Should().NotBe(0, "{0}", output);

        // The whole userinfo, not only its password half: the user is the role this
        // recipe exists to keep out of a log, and an entry that says "without echoing its
        // userinfo" is a claim about both.
        output.Should().NotContain("hunter2");
        output.Should().NotContain("learnstack_app", "the userinfo is redacted whole");
        output.Should().Contain("key/value", "the message names the form that would work");
    }

    /// <summary>
    /// Runs the repo-root <c>migrate</c> target with a given migration credential
    /// and returns its exit code and combined output.
    /// </summary>
    /// <remarks>
    /// Every value the callers pass is refused by the role check, which runs before
    /// the recipe restores a tool or opens a socket — so this touches no database
    /// and needs no Docker.
    /// </remarks>
    private static (int ExitCode, string Output) RunMigrateTarget(string migrationConnectionString)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("make")
        {
            WorkingDirectory = RepositoryPaths.RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("migrate");
        startInfo.Environment["ConnectionStrings__Migration"] = migrationConnectionString;

        // Not skipped when `make` is absent — a skipped architecture test is a
        // bug by policy, and a machine without make cannot run `make install`,
        // `make test` or `make migrate` either, so the documented workflow is
        // already broken there. What is worth fixing is the message: the raw
        // Win32Exception says "No such file or directory" and names nothing.
        System.Diagnostics.Process process;

        try
        {
            process = System.Diagnostics.Process.Start(startInfo)
                ?? throw new InvalidOperationException("`make` did not start.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException(
                "`make` is not on PATH. The repo-root Makefile is the documented entry "
                + "point for restoring, testing and migrating (see .github/CONTRIBUTING.md "
                + "§ Local checks before pushing), and this rule executes its `migrate` "
                + "target to prove the credential guard refuses what it claims to.",
                exception);
        }

        using var _ = process;

        // Both pipes drained concurrently, then the wait. Reading one to the end and only
        // then the other deadlocks whenever the child fills the second pipe's buffer while
        // this side is blocked on the first — the classic Process pitfall. The role check
        // is terse enough that it has never happened here, which is exactly why it would
        // land on whoever makes the target chattier.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        process.WaitForExit(milliseconds: 60_000).Should().BeTrue("the role check exits immediately");

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        return (process.ExitCode, stdout + stderr);
    }

    /// <summary>
    /// The body of the repo-root Makefile's <c>migrate</c> target.
    /// </summary>
    private static string ReadMigrateRecipe()
    {
        var lines = File.ReadAllLines(Path.Combine(RepositoryPaths.RepoRoot(), "Makefile"));
        var start = Array.FindIndex(lines, line => line.StartsWith("migrate:", StringComparison.Ordinal));

        start.Should().BeGreaterThanOrEqualTo(0, "the Makefile carries a `migrate` target");

        var body = lines.Skip(start + 1).TakeWhile(line => line.StartsWith('\t'));
        return string.Join('\n', body);
    }

    /// <summary>
    /// True when the recipe names the project directly or through a glob that
    /// covers it.
    /// </summary>
    /// <remarks>
    /// A glob segment is matched by translating <c>*</c> to "anything but a
    /// separator", which is what the shell does. Comparing the literal string
    /// would fail on the module loop, which is a glob by design — one entry per
    /// module would be the maintenance burden this rule exists to remove.
    /// </remarks>
    private static bool RecipeReaches(string recipe, string projectPath) =>
        recipe
            .Split([' ', '\n', '\t', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.StartsWith("backend/src", StringComparison.Ordinal))
            .Any(token => System.Text.RegularExpressions.Regex.IsMatch(
                projectPath,
                "^" + string.Join(
                    "[^/]*",
                    token.Split('*').Select(System.Text.RegularExpressions.Regex.Escape)) + "$"));
}
