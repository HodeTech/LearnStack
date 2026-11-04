using System.Diagnostics;
using System.Net;
using FluentAssertions;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BoundedAdministrationHttpTests
{
    [Fact]
    public async Task Actual_invitation_entry_transient_update_failure_is_sanitized_before_L1_and_rolls_back_both_roots()
    {
        const string canary = "invitation-provider-message-728e9";
        const string email = canary + "@example.test";
        await using var proof = await CreatePreparedAsync();
        var offer = await SeedEntryOfferAsync(proof, email: email);
        SetEntryRecipient(proof, YogaAdmin, email);
        await proof.OwnerAsync($$"""
            CREATE SEQUENCE public.invitation_sink_source;
            GRANT USAGE, SELECT ON public.invitation_sink_source TO learnstack_app;
            CREATE FUNCTION public.invitation_sink_failure() RETURNS trigger LANGUAGE plpgsql AS $proof$
            BEGIN
              IF NEW.status='Accepted' AND NEW.recipient_email='{{email}}'
                 AND EXISTS(SELECT 1 FROM public.memberships WHERE id=NEW.result_membership_id AND status='Active') THEN
                PERFORM nextval('public.invitation_sink_source');
                RAISE EXCEPTION USING ERRCODE='55000', MESSAGE=NEW.recipient_email;
              END IF;
              RETURN NEW;
            END $proof$;
            CREATE TRIGGER invitation_sink_failure BEFORE UPDATE ON public.invitations
            FOR EACH ROW EXECUTE FUNCTION public.invitation_sink_failure();
            """);
        try
        {
            using var sinks = new IdentityDiagnosticSinks();
            var capturedByHttp = new InvitationErrorCapture();
            using var client = EntryClient(proof, configure: services =>
            {
                services.Replace(ServiceDescriptor.Singleton(sinks.Loggers));
                services.Replace(ServiceDescriptor.Singleton<IErrorTrackingProvider>(capturedByHttp));
            });
            using var response = await EntrySendAsync(client, EntryToken(proof, YogaAdmin), "acceptances", offer, "\"0\"");
            await ProblemAsync(response, HttpStatusCode.ServiceUnavailable, "dependency_unavailable");
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(canary, body, StringComparison.Ordinal);
            Assert.DoesNotContain("membershipId", body, StringComparison.Ordinal);
            Assert.DoesNotContain("disposition", body, StringComparison.Ordinal);
            Assert.Null(response.Headers.ETag);
            Assert.All(await proof.AuditAsync(response, Tenant), row => Assert.NotEqual("success", row.Outcome));
            await AssertEntryStateAsync(proof, offer, "Pending", 0);
            await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString))
            {
                await SchemaQueries.AssertApplicationRoleAsync(app);
                Assert.True(await ScalarAsync<bool>(app, null, "SELECT is_called AND last_value=1 FROM public.invitation_sink_source"));
                await using var transaction = await app.BeginTransactionAsync();
                await ScopeAsync(app, transaction, Tenant.TenantId.Value);
                Assert.Equal(0L, await ScalarAsync<long>(app, transaction,
                    "SELECT count(*) FROM public.memberships WHERE user_id=@actor", ("actor", YogaAdmin.Value)));
                Assert.True(await ScalarAsync<bool>(app, transaction,
                    "SELECT row_version=0 AND accepted_at IS NULL AND result_membership_id IS NULL FROM public.invitations WHERE id=@id",
                    ("id", offer.Id)));
            }
            var failure = Assert.IsType<IdentityStorageProviderException>(Assert.Single(capturedByHttp.Failures));
            Assert.Null(failure.InnerException);
            // Reuse the existing actual L1/local-file/Sentry serialization harness
            // with the exact exception received by the real HTTP L1 boundary.
            await sinks.CaptureThroughL1Async(failure);
            var captured = await sinks.ReadAsync();
            captured.AssertPositiveControls();
            captured.AssertExcludes([canary]);
            sinks.AssertCompleteEfTraceCoverage();
            sinks.Traces.Where(trace => trace.Source == IdentityDiagnosticSinks.EfSource && trace.Status == ActivityStatusCode.Error)
                .Should().NotBeEmpty().And.OnlyContain(trace => trace.Description == "Identity storage command failed.");
            sinks.LogText.Should().Contain("IdentityStorageSaveFailed");
        }
        finally
        {
            await proof.OwnerAsync("""
                DROP TRIGGER invitation_sink_failure ON public.invitations;
                DROP FUNCTION public.invitation_sink_failure();
                DROP SEQUENCE public.invitation_sink_source;
                """);
        }
    }

    private sealed class InvitationErrorCapture : IErrorTrackingProvider
    {
        internal List<Exception> Failures { get; } = [];
        public ValueTask CaptureAsync(Exception exception, CapturedContext context, CancellationToken cancellationToken = default)
        {
            Failures.Add(exception);
            return ValueTask.CompletedTask;
        }
    }
}
