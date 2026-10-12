using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Api.Messaging;
using LearnStack.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string ApiAssembly = "LearnStack.Api";
    private const string Observation = "LearnStack.Api.Messaging.OutboxObservation";
    private const string Health = "LearnStack.Api.Messaging.OutboxHealthCheck";
    private static readonly string[] ObservationSubjects =
    [
        Observation, Health, "LearnStack.Api.Messaging.OutboxObservationSnapshot",
        "LearnStack.Api.Messaging.OutboxProcessor", "LearnStack.Api.Messaging.OutboxPublishAttempt",
    ];

    /// <summary>
    /// ADR-0046: the real worker/check registrations and memory-only observation graph
    /// cannot disappear or acquire persistence/service-locator capabilities. Runtime
    /// tests separately prove deadlines, snapshot values, diagnostics and stale health.
    /// </summary>
    [Fact]
    public void Outbox_Health_And_Metrics_Are_Bounded()
    {
        using var census = ProductionCensus.Read();
        ObservationOffenders(census).Should().BeEmpty(
            "Fix: retain the production worker/observation subjects and keep gauge/health helpers memory-only");
        ObservationRegistrationOffenders(ObservationServices()).Should().BeEmpty(
            "Fix: register exactly one mandatory dispatcher and private outbox health check without forcing its lazy source");
    }

    [Fact]
    public void Observation_walk_detects_persistence_locators_async_and_polymorphic_helpers()
    {
        using var census = ProductionCensus.Read();
        var application = census.Modules.Single(module => module.Assembly.Name.Name == "LearnStack.Application");
        foreach (var rootName in new[] { Observation, Health })
            foreach (var capability in new[] { typeof(Npgsql.NpgsqlCommand), typeof(System.Data.Common.DbConnection),
                         typeof(IServiceProvider), typeof(IServiceScopeFactory) })
            {
                AssertMutation(census, "a cross-assembly observation helper with " + capability.Name, () =>
                {
                    var root = census.Types[(ApiAssembly, rootName)];
                    var helper = AddProbe(application, "ObservationPersistenceHelper");
                    helper.Fields.Add(new FieldDefinition("Capability", FieldAttributes.Public,
                        application.ImportReference(capability)));
                    var field = new FieldDefinition("ReviewHelper", FieldAttributes.Private, root.Module.ImportReference(helper));
                    root.Fields.Add(field);
                    return () => { root.Fields.Remove(field); application.Types.Remove(helper); };
                }, ObservationOffenders);
            }
        foreach (var isInterface in new[] { true, false })
            AssertMutation(census, "a polymorphic observation helper", () =>
            {
                var root = census.Types[(ApiAssembly, Observation)];
                var contract = AddProbe(application, "ObservationContract");
                if (isInterface) contract.Attributes |= TypeAttributes.Interface | TypeAttributes.Abstract;
                var implementation = AddProbe(application, "ObservationImplementation");
                if (isInterface) implementation.Interfaces.Add(new InterfaceImplementation(contract));
                else implementation.BaseType = contract;
                var getter = application.ImportReference(typeof(IServiceScopeFactory).GetMethod(nameof(IServiceScopeFactory.CreateScope))!);
                var undoAsync = AddAsyncCall(implementation, "Read", getter);
                var field = new FieldDefinition("ReviewHelper", FieldAttributes.Private, root.Module.ImportReference(contract));
                root.Fields.Add(field);
                return () =>
                {
                    root.Fields.Remove(field);
                    undoAsync();
                    application.Types.Remove(implementation);
                    application.Types.Remove(contract);
                };
            }, ObservationOffenders);
        foreach (var subject in ObservationSubjects)
            AssertMutation(census, "a missing observation subject " + subject, () =>
            {
                var type = census.Types[(ApiAssembly, subject)];
                var module = type.Module;
                module.Types.Remove(type);
                return () => module.Types.Add(type);
            }, ObservationOffenders);
    }

    [Fact]
    public void Observation_walk_keeps_clean_memory_helpers_and_checks_real_gauge_callbacks()
    {
        using var census = ProductionCensus.Read();
        var observation = census.Types[(ApiAssembly, Observation)];
        var callbacks = observation.Methods.SelectMany(method => method.HasBody
                ? method.Body.Instructions : Enumerable.Empty<Instruction>())
            .Where(instruction => instruction.OpCode == OpCodes.Ldftn && instruction.Operand is MethodReference)
            .Select(instruction => (MethodReference)instruction.Operand)
            .Where(method => SameType(method.DeclaringType, observation)).ToArray();
        callbacks.Should().HaveCountGreaterThanOrEqualTo(4,
            "Fix: the census must inspect the shipped observable callbacks, not an empty test-only graph");
        foreach (var callback in callbacks)
            AssertMutation(census, "persistence inside an actual observation callback", () =>
            {
                var method = observation.Methods.Single(method => method.FullName == callback.FullName);
                var reference = observation.Module.ImportReference(typeof(Npgsql.NpgsqlCommand).GetConstructor(Type.EmptyTypes)!);
                var instruction = Instruction.Create(OpCodes.Newobj, reference);
                method.Body.Instructions.Insert(0, instruction);
                return () => method.Body.Instructions.Remove(instruction);
            }, ObservationOffenders);

        var helper = AddProbe(observation.Module, "MemoryObservationHelper");
        var field = new FieldDefinition("ReviewHelper", FieldAttributes.Private, helper);
        helper.Fields.Add(new FieldDefinition("Count", FieldAttributes.Public, helper.Module.TypeSystem.Int32));
        observation.Fields.Add(field);
        try { ObservationOffenders(census).Should().BeEmpty("Fix: pure memory helpers must remain admitted"); }
        finally { observation.Fields.Remove(field); observation.Module.Types.Remove(helper); }
    }

    [Fact]
    public void Observation_registration_control_detects_missing_duplicate_or_replaced_worker_and_check()
    {
        IServiceCollection services = ObservationServices();
        ObservationRegistrationOffenders(services).Should().BeEmpty();
        var worker = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(OutboxProcessor));
        services.Remove(worker);
        ObservationRegistrationOffenders(services).Should().NotBeEmpty("Fix: missing mandatory execution must be detected");
        services.Add(worker);
        services.Add(worker);
        ObservationRegistrationOffenders(services).Should().NotBeEmpty("Fix: duplicate workers must be detected");
        services.Remove(worker);
        foreach (var replacement in new[] { "missing", "duplicate", "wrong-factory" })
        {
            var modified = ObservationServices();
            modified.PostConfigure<HealthCheckServiceOptions>(options =>
            {
                var registration = options.Registrations.Single(item => item.Name == OutboxHealthCheck.Name);
                options.Registrations.Remove(registration);
                if (replacement != "missing")
                    options.Registrations.Add(new HealthCheckRegistration(OutboxHealthCheck.Name,
                        _ => new UnrelatedHealthCheck(), HealthStatus.Unhealthy, []));
                if (replacement == "duplicate") options.Registrations.Add(registration);
            });
            ObservationRegistrationOffenders(modified).Should().NotBeEmpty("Fix: detect " + replacement + " health registration");
        }
        ObservationRegistrationOffenders(ObservationServices()).Should().BeEmpty();
    }

    /// <summary>
    /// ADR-0046: all literal SQL targeting outbox row mutations belongs to the producer,
    /// exact transition methods or the one legacy backfill. This structural companion
    /// does not claim to analyze dynamically constructed table names or SQL dataflow.
    /// Real PostgreSQL tests prove predicates, fencing and atomicity.
    /// </summary>
    [Fact]
    public void Outbox_Retry_And_Terminal_Transitions_Are_Fenced()
    {
        using var census = ProductionCensus.Read();
        OutboxSqlOffenders(census).Should().BeEmpty(
            "Fix: keep outbox literal INSERT/status mutation SQL in the exact producer, dispatcher and backfill methods");
    }

    [Fact]
    public void Outbox_SQL_census_detects_quoted_schema_CTE_extra_and_missing_writer_paths()
    {
        using var census = ProductionCensus.Read();
        var application = census.Modules.Single(module => module.Assembly.Name.Name == "LearnStack.Application");
        foreach (var sql in new[]
                 {
                     "update outbox_messages set attempts = 0", "UPDATE ONLY public.\"outbox_messages\" SET attempts=0",
                     "WITH c AS (SELECT 1) UpDaTe \"public\" . \"outbox_messages\" SET attempts=0",
                     "INSERT INTO public.outbox_messages (id) VALUES (NULL)", "DELETE FROM \"outbox_messages\"",
                     "DELETE FROM ONLY public.outbox_messages", "UPDATE /* review */ public.outbox_messages SET attempts=0",
                     "MERGE INTO outbox_messages USING x ON true WHEN MATCHED THEN DELETE",
                     "COPY public.outbox_messages FROM STDIN", "TRUNCATE TABLE public.outbox_messages",
                 })
            AssertMutation(census, "an unauthorized literal SQL writer: " + sql, () => AddSqlProbe(application, sql), OutboxSqlOffenders);
        AssertMutation(census, "an extra helper on the allowed store", () =>
        {
            var store = census.Types[(InfrastructureAssembly, Store)];
            var method = AddSqlMethod(store, "UnexpectedWrite", "UPDATE outbox_messages SET attempts=0");
            return () => store.Methods.Remove(method);
        }, OutboxSqlOffenders);
        AssertMutation(census, "an extra overload of an allowed transition", () =>
        {
            var store = census.Types[(InfrastructureAssembly, Store)];
            var method = AddSqlMethod(store, "AcquireAsync", "UPDATE outbox_messages SET attempts=0");
            return () => store.Methods.Remove(method);
        }, OutboxSqlOffenders);
        AssertMutation(census, "a missing production mutation path", () =>
        {
            var literal = OwnedBodies(census).Where(item => item.Owner.DeclaringType.FullName == Store)
                .SelectMany(item => item.Body.Body.Instructions)
                .First(instruction => instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string sql && OutboxMutation.IsMatch(sql));
            var original = literal.Operand;
            literal.Operand = "SELECT 1";
            return () => literal.Operand = original;
        }, OutboxSqlOffenders);
    }

    [Fact]
    public void Outbox_SQL_census_preserves_reads_and_unrelated_table_writes()
    {
        using var census = ProductionCensus.Read();
        var application = census.Modules.Single(module => module.Assembly.Name.Name == "LearnStack.Application");
        foreach (var sql in new[] { "SELECT * FROM outbox_messages FOR UPDATE SKIP LOCKED",
                     "UPDATE another_outbox_messages SET attempts=0", "INSERT INTO outbox_messages_archive VALUES (1)" })
        {
            var undo = AddSqlProbe(application, sql);
            try { OutboxSqlOffenders(census).Should().BeEmpty("Fix: admitted reads and unrelated table writes must stay clean"); }
            finally { undo(); }
        }
    }

    private static readonly Regex OutboxMutation = new(
        "\\b(?:INSERT\\s+INTO|UPDATE|DELETE\\s+FROM|MERGE\\s+INTO|COPY|TRUNCATE(?:\\s+TABLE)?)"
        + "(?:\\s|/\\*.*?\\*/|--[^\\r\\n]*(?:\\r?\\n|$))+(?:ONLY\\s+)?"
        + "(?:(?:\"[^\"]+\"|[a-z_][a-z0-9_$]*)\\s*\\.\\s*)?(?:\"outbox_messages\"|outbox_messages)(?=\\s|[;(]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1));

    private static SortedSet<string> OutboxSqlOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        CheckSet(offenders, "SQL production assemblies", census.Modules.Select(module => module.Assembly.Name.Name), ProductionAssemblies.Names());
        var writers = OwnedBodies(census).SelectMany(item => item.Body.Body.Instructions
            .Where(instruction => instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string sql && OutboxMutation.IsMatch(sql))
            .Select(_ => MethodId(item.Owner)));
        var expected = new List<string>();
        foreach (var (typeName, methodName) in new[]
        {
            ("LearnStack.Infrastructure.Messaging.PostgresOutbox", "EnqueueAsync"),
            (Store, "AcquireAsync"), (Store, "UpdateAsync"), (Store, "TryTerminalizeAsync"),
            ("LearnStack.Infrastructure.Persistence.Migrations.add_outbox_dispatch_state", "Up"),
        })
        {
            if (!census.Types.TryGetValue((InfrastructureAssembly, typeName), out var type))
            { offenders.Add("Missing SQL writer type: " + typeName); continue; }
            var methods = type.Methods.Where(method => method.Name == methodName).ToArray();
            if (methods.Length != 1) offenders.Add("Missing or ambiguous SQL writer: " + typeName + "." + methodName);
            else expected.Add(MethodId(methods[0]));
        }
        CheckSet(offenders, "literal outbox writer methods", writers, expected);
        return offenders;
    }

    private static IEnumerable<(MethodDefinition Body, MethodDefinition Owner)> OwnedBodies(ProductionCensus census)
    {
        var owners = census.Types.Values.SelectMany(type => type.Methods)
            .SelectMany(method => method.CustomAttributes.Where(attribute => attribute.AttributeType.FullName is
                    "System.Runtime.CompilerServices.AsyncStateMachineAttribute" or "System.Runtime.CompilerServices.IteratorStateMachineAttribute"
                    or "System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute")
                .Where(attribute => attribute.ConstructorArguments.FirstOrDefault().Value is TypeReference)
                .Select(attribute => (Type: Key((TypeReference)attribute.ConstructorArguments[0].Value), Method: method)))
            .ToDictionary(item => item.Type, item => item.Method);
        foreach (var method in census.Types.Values.SelectMany(type => type.Methods).Where(method => method.HasBody))
            yield return (method, method.Name == "MoveNext" && owners.TryGetValue(Key(method.DeclaringType), out var owner) ? owner : method);
    }

    private static SortedSet<string> ObservationOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        CheckSet(offenders, "observation production assemblies", census.Modules.Select(module => module.Assembly.Name.Name), ProductionAssemblies.Names());
        foreach (var subject in ObservationSubjects)
            if (!types.ContainsKey((ApiAssembly, subject))) offenders.Add("Missing observation subject: " + subject);
        if (offenders.Count != 0) return offenders;
        var pending = new Queue<TypeDefinition>(new[] { types[(ApiAssembly, Observation)], types[(ApiAssembly, Health)] });
        var visited = new HashSet<(string Assembly, string Type)>();
        while (pending.TryDequeue(out var type))
        {
            if (!visited.Add(Key(type))) continue;
            foreach (var reference in Il.ReferencedTypeReferences(type))
            {
                if (reference.Namespace.StartsWith("Npgsql", StringComparison.Ordinal)
                    || reference.Namespace.StartsWith("System.Data", StringComparison.Ordinal)
                    || reference.Namespace.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                    || reference.FullName is "System.IServiceProvider" or "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory"
                        or "Microsoft.Extensions.DependencyInjection.IKeyedServiceProvider")
                    offenders.Add(TypeId(type) + " -> " + reference.FullName);
                if (!types.TryGetValue(Key(reference), out var helper)) continue;
                pending.Enqueue(helper);
                if (!helper.IsSealed)
                    foreach (var implementation in types.Values.Where(candidate => !candidate.IsInterface && Derives(candidate, helper, types)))
                        pending.Enqueue(implementation);
            }
        }
        return offenders;
    }

    private static ServiceCollection ObservationServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(IntegrationEventHandlerRegistry.Discover());
        services.AddLearnStackPersistence(new ConfigurationBuilder().Build());
        return services;
    }

    private static SortedSet<string> ObservationRegistrationOffenders(IServiceCollection services)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var workers = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(OutboxProcessor)).ToArray();
        if (workers.Length != 1 || workers[0].Lifetime != ServiceLifetime.Singleton) offenders.Add("Mandatory singleton worker registration");
        using var provider = services.BuildServiceProvider();
        var checks = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Where(check => check.Name == OutboxHealthCheck.Name).ToArray();
        if (checks.Length != 1 || checks[0].Factory(provider).GetType() != typeof(OutboxHealthCheck)) offenders.Add("Private outbox health registration");
        if (provider.GetRequiredKeyedService<Lazy<Npgsql.NpgsqlDataSource>>(OutboxDispatcherDataSource.Key).IsValueCreated)
            offenders.Add("Observation forced dispatcher source construction");
        return offenders;
    }

    private static Action AddSqlProbe(ModuleDefinition module, string sql)
    {
        var type = AddProbe(module, "OutboxSqlWriter");
        AddSqlMethod(type, "Write", sql);
        return () => module.Types.Remove(type);
    }

    private static MethodDefinition AddSqlMethod(TypeDefinition type, string name, string sql)
    {
        var method = new MethodDefinition(name, MethodAttributes.Public | MethodAttributes.Static, type.Module.TypeSystem.Void);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, sql));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Pop));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        type.Methods.Add(method);
        return method;
    }

    private sealed class UnrelatedHealthCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(HealthCheckResult.Healthy());
    }
}
