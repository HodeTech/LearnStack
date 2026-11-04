using System.Diagnostics;
using System.Text.Json;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(IdentityDiagnosticSinkGroup.Name)]
public sealed class MembershipProfileLocaleDiagnosticTests(MembershipAdministrationFixture fixture)
    : IClassFixture<MembershipAdministrationFixture>
{
    [Fact]
    public async Task The_owning_locale_reader_preserves_cancellation_and_programming_failures()
    {
        await using var harness = await fixture.CreateAsync("membership-locale-failure-kinds");
        await using var frame = await harness.Unit.BeginTransactionAsync();
        await harness.Unit.SetTenantContextAsync(harness.Services.GetRequiredService<ITenantContext>());
        var reader = harness.Services.GetRequiredService<ITenantLocaleEligibilityReader>();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadEligibleAsync("en", cancelled.Token));
        Assert.Equal(cancelled.Token, cancellation.CancellationToken);
        await harness.Services.GetRequiredService<TenancyDbContext>().DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.ReadEligibleAsync("en", CancellationToken.None));
        await frame.FailAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Actual_profile_locale_failure_contains_stored_values_and_its_unprotected_twin_proves_the_diagnostic_detector(
        bool protectedBoundary, bool own)
    {
        var member = await fixture.AddMemberAsync();
        var canaries = new[] { "locale-neutral-493fd", "locale-nested-248ba", "locale-secret-972ca" };
        var payload = JsonSerializer.Serialize(new { value = canaries[0], items = new[] { new { value = canaries[1], token = canaries[2] } } });
        await fixture.MutateAsync(SchemaFixture.TenantA, null, null, true, (app, transaction) =>
            ExecuteAsync(app, transaction, "UPDATE tenants SET display_name=@payload WHERE id=@tenant",
                ("payload", payload), ("tenant", SchemaFixture.TenantA)));
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.OwnerConnectionString);
        var policiesBefore = await LocalePoliciesAsync(owner);
        await ExecuteAsync(owner, null, $$"""
            CREATE FUNCTION membership_profile_locale_sink_fault() RETURNS boolean
                LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
            DECLARE payload text;
            BEGIN
                SELECT display_name INTO STRICT payload FROM public.tenants WHERE id='{{SchemaFixture.TenantA:D}}'::uuid;
                RAISE EXCEPTION USING ERRCODE='P0399',MESSAGE=payload;
            END $$;
            REVOKE ALL ON FUNCTION membership_profile_locale_sink_fault() FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION membership_profile_locale_sink_fault() TO learnstack_app;
            CREATE POLICY membership_profile_locale_sink_fault ON tenant_locales
                AS RESTRICTIVE FOR SELECT TO learnstack_app USING(membership_profile_locale_sink_fault());
            """);
        try
        {
            // This direct app-role control establishes that the unchanged policy
            // really reads retained human text and raises it as a provider message.
            await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString))
            {
                await SchemaQueries.AssertApplicationRoleAsync(app);
                await using var transaction = await app.BeginTransactionAsync();
                await ScopeAsync(app, transaction, SchemaFixture.TenantA);
                var provider = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync<string>(app, transaction,
                    "SELECT locale FROM tenant_locales WHERE tenant_id=@tenant LIMIT 1", ("tenant", SchemaFixture.TenantA)));
                Assert.Equal("P0399", provider.SqlState);
                Assert.Equal(payload, provider.MessageText);
                await transaction.RollbackAsync();
            }
            using var sinks = new IdentityDiagnosticSinks();
            using var callers = new ActivitySource("learnstack.identity.storage-qualification");
            using var caller = callers.StartActivity("membership-profile-locale-read");
            Assert.NotNull(caller);
            await using var harness = await RoleGrantPipelineHarness.CreateAsync(fixture.AppConnectionString, Authority,
                TenantId.From(SchemaFixture.TenantA), UserId.From(own ? member.Actor : AdminA), "membership-profile-locale-sink", configure: services =>
                {
                    services.AddSingleton<ILoggerFactory>(sinks.Loggers);
                    services.AddModuleDbContext<TenancyDbContext>();
                    services.AddScoped<TenantLocaleEligibilityReader>();
                    services.AddScoped<ObservedLocaleEligibilityReader>(provider => new(protectedBoundary
                        ? provider.GetRequiredService<TenantLocaleEligibilityReader>()
                        : new UnprotectedLocaleReader(provider.GetRequiredService<TenancyDbContext>(), provider.GetRequiredService<ITenantContext>())));
                    services.AddScoped<ITenantLocaleEligibilityReader>(provider => provider.GetRequiredService<ObservedLocaleEligibilityReader>());
                });
            Result<MembershipMutationResult>? outcome = null;
            IRequest<Result<MembershipMutationResult>> request = own
                ? new SetOwnMembershipProfileCommand(0, "uncommitted-profile", "tr")
                : new SetMembershipProfileCommand(member.Membership, 0, "uncommitted-profile", "tr");
            var escaped = await Record.ExceptionAsync(async () => outcome = await harness.Sender.Send(
                request));
            var observed = harness.Services.GetRequiredService<ObservedLocaleEligibilityReader>();
            Assert.NotNull(observed.Failure);
            caller!.Stop();
            sinks.AssertCompleteEfTraceCoverage();
            IdentityDiagnosticSinks.AssertEfCallerAncestry(sinks.Traces, caller);
            // The real caller maps the owning-port exception to Result.Fail.
            // Exercise the same observed exception through the actual L1 vehicle
            // as well, so containment is proved even if a different caller lets it escape.
            var translated = observed.Failure is TenantLocaleEligibilityReadException;
            await sinks.CaptureThroughL1Async(observed.Failure!, translated ? 503 : 500);
            var captured = await sinks.ReadAsync();
            captured.AssertPositiveControls(translated ? "dependency_unavailable" : "internal_error");
            if (protectedBoundary)
            {
                captured.AssertExcludes(canaries);
                Assert.Contains("TenantLocaleEligibilityReadFailed", captured.Log, StringComparison.Ordinal);
                Assert.DoesNotContain("QueryIterationFailed", captured.Log, StringComparison.Ordinal);
                Assert.Null(escaped);
                Assert.NotNull(outcome);
                Assert.True(outcome.IsFailure);
                Assert.Equal("dependency_unavailable", outcome.Error!.Code);
                var fixedFailure = Assert.IsType<TenantLocaleEligibilityReadException>(observed.Failure);
                Assert.Null(fixedFailure.InnerException);
                Assert.Equal("Tenant locale eligibility is unavailable.", fixedFailure.Message);
            }
            else
            {
                Assert.NotNull(escaped);
                Assert.Null(outcome);
                Assert.Equal(payload, Assert.IsType<PostgresException>(observed.Failure).MessageText);
                Assert.Contains("QueryIterationFailed", captured.Log, StringComparison.Ordinal);
                foreach (var canary in canaries)
                {
                    Assert.Contains(canary, captured.Log, StringComparison.Ordinal);
                    Assert.Contains(canary, captured.Local, StringComparison.Ordinal);
                    Assert.Contains(canary, captured.Sentry, StringComparison.Ordinal);
                    Assert.Contains(canary, captured.Trace, StringComparison.Ordinal);
                    Assert.DoesNotContain(canary, captured.Response, StringComparison.Ordinal);
                }
            }
            var state = await fixture.ReadAsync(member.Membership);
            Assert.Equal(0, state.Version);
            Assert.Equal("profile-canary", state.DisplayName);
            Assert.Equal("en", state.PreferredLocale);
        }
        finally
        {
            await ExecuteAsync(owner, null, "DROP POLICY membership_profile_locale_sink_fault ON tenant_locales; DROP FUNCTION membership_profile_locale_sink_fault()");
        }
        Assert.Equal(policiesBefore, await LocalePoliciesAsync(owner));
        Assert.True((await fixture.SendAsync(new SetMembershipProfileCommand(member.Membership, 0, "clean-profile", "tr"),
            "membership-profile-locale-clean")).IsSuccess);
    }

    private static Task<string> LocalePoliciesAsync(NpgsqlConnection owner) => ScalarAsync<string>(owner, null, """
        SELECT jsonb_agg(jsonb_build_array(polname,polpermissive,polroles,pg_get_expr(polqual,polrelid),pg_get_expr(polwithcheck,polrelid)) ORDER BY polname)::text
        FROM pg_policy WHERE polrelid='public.tenant_locales'::regclass
        """);

    private sealed class ObservedLocaleEligibilityReader(ITenantLocaleEligibilityReader inner) : ITenantLocaleEligibilityReader
    {
        public Exception? Failure { get; private set; }
        public async Task<Result<string>> ReadEligibleAsync(string locale, CancellationToken cancellationToken)
        {
            try { return await inner.ReadEligibleAsync(locale, cancellationToken); }
            catch (Exception failure) { Failure = failure; throw; }
        }
    }

    /// <summary>Fixture-only copy of the former unprotected query; proves the sinks can observe the exact stored payload.</summary>
    private sealed class UnprotectedLocaleReader(TenancyDbContext db, ITenantContext context) : ITenantLocaleEligibilityReader
    {
        public async Task<Result<string>> ReadEligibleAsync(string locale, CancellationToken cancellationToken)
        {
            await db.Tenants.AsNoTracking().Include(tenant => tenant.Locales)
                .SingleOrDefaultAsync(tenant => tenant.Id == context.TenantId && tenant.DeletedAt == null, cancellationToken);
            throw new InvalidOperationException("The planted locale read fault did not execute.");
        }
    }
}
