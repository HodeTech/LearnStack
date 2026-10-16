using System.Reflection;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

[Collection(HangfireGlobalConfigurationIsolation.Name)]
public sealed class HangfireInvocationContractTests
{
    private static BackgroundJobId Id => BackgroundJobId.From(Guid.Parse("11111111-1111-7111-8111-111111111111"));
    private static BackgroundJobWorkerPoolRegistration Pool => BackgroundJobInfrastructureRegistry.CreateDefault().Pools["transactional"];

    [Fact]
    public void Exact_instance_target_arguments_and_queue_are_admitted()
    {
        var job = new Job(typeof(HangfireJobWakeup), HangfireInvocationContract.JobMethod, [Id, 1L], "transactional");
        var validate = () => HangfireInvocationContract.Validate(job, Pool, BackgroundJobRecurrenceRegistry.CreateEmpty());
        validate.Should().NotThrow();
        typeof(HangfireJobWakeup).GetConstructors().Should().BeEmpty("only the closed activator constructs wrappers");
        typeof(HangfireRecurrenceTick).GetConstructors().Should().BeEmpty();
        foreach (var wrapper in new[] { typeof(HangfireJobWakeup), typeof(HangfireRecurrenceTick) })
            wrapper.GetCustomAttribute<AutomaticRetryAttribute>().Should().NotBeNull()
                .And.Subject.As<AutomaticRetryAttribute>().Attempts.Should().Be(0);
    }

    [Theory]
    [InlineData("static")]
    [InlineData("inherited")]
    [InlineData("wrong_queue")]
    [InlineData("missing_queue")]
    [InlineData("zero_generation")]
    [InlineData("wrong_argument_type")]
    public void Stock_Job_shapes_that_do_not_match_the_closed_invocation_are_refused(string mutation)
    {
        var type = mutation switch
        {
            "static" => typeof(StaticTarget),
            "inherited" => typeof(InheritedTarget),
            _ => typeof(HangfireJobWakeup),
        };
        var method = mutation switch
        {
            "static" => typeof(StaticTarget).GetMethod(nameof(StaticTarget.RunAsync))!, // Exact fixture method.
            "inherited" => typeof(BaseTarget).GetMethod(nameof(BaseTarget.RunAsync))!, // Deliberately inherited.
            _ => HangfireInvocationContract.JobMethod,
        };
        object first = mutation == "wrong_argument_type" ? Id.Value.ToString() : Id;
        var queue = mutation switch { "wrong_queue" => "external", "missing_queue" => null, _ => "transactional" };
        var job = new Job(type, method, [first, mutation == "zero_generation" ? 0L : 1L], queue);
        var validate = () => HangfireInvocationContract.Validate(job, Pool, BackgroundJobRecurrenceRegistry.CreateEmpty());
        validate.Should().Throw<InvalidOperationException>().WithMessage("*invocation contract*");
    }

    [Theory]
    [InlineData("System.IO.File, System.Private.CoreLib")]
    [InlineData("LearnStack.Infrastructure.Jobs.HangfireJobWakeup, SpoofedAssembly")]
    [InlineData("LearnStack.Infrastructure.Jobs.HangfireJobWakeup")]
    [InlineData("System.Object")]
    public void Unknown_type_tokens_are_refused_without_assembly_resolution(string token)
    {
        var resolutions = 0;
        ResolveEventHandler observer = (_, _) => { resolutions++; return null; };
        AppDomain.CurrentDomain.AssemblyResolve += observer;
        try
        {
            var resolve = () => HangfireInvocationContract.ResolveType(token);
            resolve.Should().Throw<InvalidOperationException>().Which.Message.Should().NotContain(token);
            resolutions.Should().Be(0);
        }
        finally { AppDomain.CurrentDomain.AssemblyResolve -= observer; }
    }

    [Fact]
    public void Positive_closed_resolver_tokens_return_only_the_five_fixed_types()
    {
        HangfireInvocationContract.ResolveType(HangfireStorageContract.JobWakeupType).Should().Be<HangfireJobWakeup>();
        HangfireInvocationContract.ResolveType(HangfireStorageContract.RecurrenceTickType).Should().Be<HangfireRecurrenceTick>();
        HangfireInvocationContract.ResolveType("LearnStack.SharedKernel.Jobs.BackgroundJobId, LearnStack.SharedKernel").Should().Be<BackgroundJobId>();
        HangfireInvocationContract.ResolveType("System.String").Should().Be<string>();
        HangfireInvocationContract.ResolveType("System.Int64").Should().Be<long>();
    }

    [Fact]
    public void The_empty_production_recurrence_registry_cannot_activate_an_arbitrary_tick()
    {
        var job = new Job(typeof(HangfireRecurrenceTick), HangfireInvocationContract.TickMethod, ["fixture.tenants", 1L], "control");
        var pool = BackgroundJobInfrastructureRegistry.CreateDefault().Pools["control"];
        var validate = () => HangfireInvocationContract.Validate(job, pool, BackgroundJobRecurrenceRegistry.CreateEmpty());
        validate.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Closed_activator_binds_one_pool_and_one_invocation_and_scope_disposal_revokes_entry()
    {
        var definitions = BackgroundJobDefinitionRegistry.CreateEmpty();
        var topology = definitions.Infrastructure;
        var options = new BackgroundJobRuntimeOptions();
        using var source = new PrivilegedDataSource(() => throw new InvalidOperationException("Must not resolve a credential."));
        var routing = new PostgresBackgroundJobRoutingStore(source, definitions, options, new SystemGuidFactory());
        var scopes = Substitute.For<IServiceScopeFactory>();
        var executor = new JobExecutor(scopes, Substitute.For<ITenantContextAccessor>(),
            new BackgroundJobExecutionAdmission(definitions, definitions, Substitute.For<IAuditCatalog>()), options);
        var activator = HangfireInvocationContract.CreateActivator(topology.Pools["transactional"], topology,
            routing, executor, BackgroundJobRecurrenceRegistry.CreateEmpty());
        var job = new Job(typeof(HangfireJobWakeup), HangfireInvocationContract.JobMethod, [Id, 1L], "transactional");
        var context = new PerformContext(null, Substitute.For<IStorageConnection>(),
            new BackgroundJob("1", job, DateTime.UtcNow), Substitute.For<IJobCancellationToken>());
        HangfireJobWakeup wrapper;
        using (var scope = activator.BeginScope(context))
        {
            var arbitrary = () => scope.Resolve(typeof(object));
            arbitrary.Should().Throw<InvalidOperationException>();
            wrapper = (HangfireJobWakeup)scope.Resolve(typeof(HangfireJobWakeup));
        }
        var stale = () => wrapper.RunAsync(Id, 1);
        await stale.Should().ThrowAsync<InvalidOperationException>();
        var direct = () => activator.ActivateJob(typeof(HangfireJobWakeup));
        direct.Should().Throw<InvalidOperationException>();
        var wrongPool = new Job(typeof(HangfireJobWakeup), HangfireInvocationContract.JobMethod, [Id, 1L], "external");
        var begin = () => activator.BeginScope(new PerformContext(null, Substitute.For<IStorageConnection>(),
            new BackgroundJob("2", wrongPool, DateTime.UtcNow), Substitute.For<IJobCancellationToken>()));
        begin.Should().Throw<InvalidOperationException>();
        source.IsValueCreated.Should().BeFalse();
        scopes.DidNotReceive().CreateScope();
    }

    public static class StaticTarget
    {
        public static Task RunAsync(BackgroundJobId id, long generation) => Task.CompletedTask;
    }
    public class BaseTarget
    {
        private int _calls;
        public Task RunAsync(BackgroundJobId id, long generation)
        {
            Interlocked.Increment(ref _calls);
            return Task.CompletedTask;
        }
    }
    public sealed class InheritedTarget : BaseTarget;
}
