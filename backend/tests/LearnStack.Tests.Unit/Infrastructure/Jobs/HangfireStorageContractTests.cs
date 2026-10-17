using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.Logging;
using Hangfire.Storage;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;
using Newtonsoft.Json;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

[Collection(HangfireGlobalConfigurationIsolation.Name)]
public sealed class HangfireStorageContractTests
{
    [Fact]
    public void Guard_contract_retains_exact_volatility_including_default_and_explicit_volatile_triggers()
    {
        var guards = HangfireStorageContract.Guards();
        guards.Should().HaveCount(11);
        guards.Where(pair => pair.Value.Volatility == "i").Select(pair => pair.Key).Should().BeEquivalentTo(
            ["fn_hangfire_queue", "fn_hangfire_server", "fn_hangfire_state_valid"]);
        guards.Where(pair => pair.Value.Volatility == "s").Select(pair => pair.Key).Should().BeEquivalentTo(
            ["fn_hangfire_recurrence", "fn_hangfire_invocation", "fn_hangfire_parameter_valid", "fn_hangfire_metadata_valid"]);
        guards.Where(pair => pair.Value.Volatility == "v").Select(pair => pair.Key).Should().BeEquivalentTo(
            ["fn_hangfire_job_guard", "fn_hangfire_state_guard", "fn_hangfire_parameter_guard", "fn_hangfire_metadata_guard"]);
    }

    [Fact]
    public void Every_provider_option_closes_runtime_ddl_reconnect_enlistment_and_degraded_startup()
    {
        var options = HangfireStorageBootstrap.Options();
        options.SchemaName.Should().Be("public");
        options.PrepareSchemaIfNecessary.Should().BeFalse();
        options.AllowDegradedModeWithoutStorage.Should().BeFalse();
        options.StartupConnectionMaxRetries.Should().Be(0);
        options.EnableTransactionScopeEnlistment.Should().BeFalse();
        options.EnableLongPolling.Should().BeFalse();
        options.UseNativeDatabaseTransactions.Should().BeTrue();
        options.UseSlidingInvisibilityTimeout.Should().BeTrue();
        options.JobExpirationCheckInterval.Should().Be(TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void The_bounded_log_boundary_never_evaluates_a_factory_or_retains_an_exception_or_logger_name()
    {
        var diagnostics = new HangfireBoundedLogProvider();
        var logger = diagnostics.GetLogger("synthetic-secret-canary");
        logger.Log(LogLevel.Error, () => throw new InvalidOperationException("Must never run"),
            new InvalidOperationException("synthetic-secret-canary")).Should().BeTrue();
        diagnostics.Count(HangfireDiagnosticLevel.Error, HangfireDiagnosticOutcome.Fault).Should().Be(1);
        logger.Log(LogLevel.Info, null!).Should().BeTrue();
        diagnostics.Count(HangfireDiagnosticLevel.Information, HangfireDiagnosticOutcome.Operational).Should().Be(0);
        logger.Log((LogLevel)int.MaxValue, () => throw new InvalidOperationException()).Should().BeFalse();
    }

    [Fact]
    public void Vendor_and_exit_logging_has_no_supplied_observer_and_only_fixed_memory_dimensions()
    {
        typeof(HangfireBoundedLogProvider).GetConstructors().Should().ContainSingle()
            .Which.GetParameters().Should().BeEmpty("vendor callbacks must not invoke an arbitrary observer");
        var diagnostics = new HangfireBoundedLogProvider();
        Parallel.For(0, 100, _ => diagnostics.GetLogger("Hangfire.PostgreSql.PostgreSqlFetchedJob")
            .Log(LogLevel.Debug, () => throw new InvalidOperationException("Must never run"),
                new InvalidOperationException("synthetic-secret-canary")));
        diagnostics.Count(HangfireDiagnosticLevel.Debug, HangfireDiagnosticOutcome.Fault,
            HangfireDiagnosticSource.FetchedJobHeartbeat).Should().Be(100);
        diagnostics.Count(HangfireDiagnosticLevel.Debug, HangfireDiagnosticOutcome.Fault,
            HangfireDiagnosticSource.Expiration).Should().Be(0);
        diagnostics.Count(HangfireDiagnosticLevel.Debug, HangfireDiagnosticOutcome.Fault).Should().Be(0);
    }

    [Fact]
    public void The_real_identifier_uses_a_canonical_opaque_string_without_a_vendor_dependency_in_the_kernel()
    {
        var id = BackgroundJobId.From(Guid.Parse("11111111-1111-7111-8111-111111111111"));
        JsonConvert.SerializeObject(id).Should().Be("\"11111111-1111-7111-8111-111111111111\"");
        JsonConvert.DeserializeObject<BackgroundJobId>(JsonConvert.SerializeObject(id)).Should().Be(id);
        TypeDescriptor.GetConverter(typeof(BackgroundJobId)).CanConvertFrom(typeof(string)).Should().BeTrue();
        var job = new Job(typeof(HangfireJobWakeup), HangfireInvocationContract.JobMethod, [id, 1L], "transactional");
        var previous = TypeHelper.CurrentTypeSerializer;
        var previousResolver = TypeHelper.CurrentTypeResolver;
        try
        {
            HangfireStorageBootstrap.ConfigureWireFormat();
            var invocation = InvocationData.SerializeJob(job);
            invocation.ParameterTypes.Should().Be("[\"LearnStack.SharedKernel.Jobs.BackgroundJobId, LearnStack.SharedKernel\",\"System.Int64\"]");
            invocation.Arguments.Should().Be("[\"\\\"11111111-1111-7111-8111-111111111111\\\"\",\"1\"]");
            invocation.DeserializeJob().Args.Should().Equal(id, 1L);
        }
        finally
        {
            TypeHelper.CurrentTypeSerializer = previous;
            TypeHelper.CurrentTypeResolver = previousResolver;
        }
    }

    [Fact]
    public void Recurring_job_payload_is_the_exact_full_stock_wire_form_under_pinned_compatibility_110()
    {
        HangfireStorageBootstrap.ConfigureWireFormat();
        var arguments = "[\"\\\"fixture.tenants\\\"\",\"1\"]";
        var invocation = new InvocationData(HangfireStorageContract.RecurrenceTickType, "RunAsync", "[\"System.String\",\"System.Int64\"]", arguments);
        var payload = invocation.SerializePayload();
        var actual = JsonConvert.DeserializeObject<InvocationData>(payload)!;
        actual.Type.Should().Be(HangfireStorageContract.RecurrenceTickType);
        actual.Method.Should().Be("RunAsync");
        actual.ParameterTypes.Should().Be("[\"System.String\",\"System.Int64\"]");
        actual.Arguments.Should().Be(arguments);
        payload.Should().Contain("\"Type\"").And.NotContain("\"t\"");
    }

    [Fact]
    public void Every_stock_bootstrap_resource_matches_the_exact_upstream_bytes()
    {
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "third-party", "hangfire", "script-sha256.json")));
        using (manifest)
        {
            var entries = manifest.RootElement.EnumerateObject().ToArray();
            entries.Should().HaveCount(21);
            foreach (var entry in entries)
            {
                using var stream = typeof(HangfireStorageContract).Assembly.GetManifestResourceStream(
                    "LearnStack.Infrastructure.Jobs.HangfireSql." + entry.Name)!;
                stream.Should().NotBeNull();
                Convert.ToHexStringLower(SHA256.HashData(stream)).Should().Be(entry.Value.GetString());
            }
        }
    }

}
