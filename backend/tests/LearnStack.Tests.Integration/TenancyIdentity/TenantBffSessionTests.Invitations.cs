using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

public sealed partial class TenantBffSessionTests
{
    [Fact]
    public Task Native_invitation_new_nonseed_registration_uses_real_verification_mail_then_separate_session() =>
        AssertNativeInvitationAsync(registration: true);

    [Fact]
    public Task Native_invitation_existing_nonseed_identity_uses_real_verification_mail_and_retains_existing_membership_session() =>
        AssertNativeInvitationAsync(registration: false);

    private async Task AssertNativeInvitationAsync(bool registration)
    {
        await using var provider = await KeycloakFixture.StartWithInvitationMailAsync();
        using var administrator = await provider.CreateAdminClientAsync();
        var realm = await administrator.GetAsync();
        realm.GetProperty("registrationEmailAsUsername").GetBoolean().Should().BeTrue();
        realm.GetProperty("duplicateEmailsAllowed").GetBoolean().Should().BeFalse();
        realm.GetProperty("editUsernameAllowed").GetBoolean().Should().BeFalse();
        var actions = await administrator.GetAsync("authentication/required-actions");
        actions.EnumerateArray().Count(action => action.GetProperty("alias").GetString() == "VERIFY_EMAIL"
            && action.GetProperty("enabled").GetBoolean()).Should().Be(1, "the pinned stock required action must actually be enabled");
        var email = "invitation-" + Guid.NewGuid().ToString("N") + "@example.test";
        if (!registration)
            (await administrator.PostAsync("users", new
            {
                username = email,
                email,
                enabled = true,
                emailVerified = false,
                firstName = "Existing",
                lastName = "Invitation",
                credentials = new[] { new { type = "password", value = KeycloakFixture.DemoPassword, temporary = false } },
            })).Should().Be(HttpStatusCode.Created);
        await using var database = await KeycloakIdentityDatabase.CreateAsync(publicDatabase, provider);
        await SeedAdministrationOriginsAsync(database);
        var tenant = SeedData.English;
        var host = tenant.Host + ":3000";
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var nativeSecret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        await using var api = StartApi(database, secret, nativeSecret, provider, invitationMail: true);
        using var inviter = provider.Browser();
        var administratorIdentity = SeedData.IdentityUsers[0];
        var authorized = await inviter.AuthorizeAsync(administratorIdentity.Username, KeycloakFixture.DemoPassword,
            "https://" + host + "/api/auth/callback/keycloak");
        authorized.Authorized.Should().BeTrue();
        var credential = await inviter.RedeemAsync(authorized);
        credential.StatusCode.Should().Be(HttpStatusCode.OK);
        var mail = await CreateAndDeliverInvitationAsync(database, api, provider, tenant, credential.AccessToken, secret, nativeSecret, email);
        await using var native = await BffNativeDriver.StartAsync(RepositoryRoot(), NativeConfiguration(provider, database, api, secret, nativeSecret));
        if (registration) await native.InvitationBrowserAsync(host, mail.Code);
        using var browser = provider.Browser();
        var cookies = new BffWireCookies();
        if (!registration) await CreateExistingInvitationIdentityAsync(native, browser, host, cookies, email);

        var landing = await native.RequestAsync(host, "/invitations", cookies.Header);
        landing.Status.Should().Be(200);
        landing.Data.GetProperty("state").GetString().Should().Be("landing");
        cookies.Apply(landing);
        var captured = await native.RequestAsync(host, "/api/auth/invitation/capture", cookies.Header, "POST",
            Form("csrfToken", landing.Data.GetProperty("csrfToken").GetString()!) + "&" + Form("code", mail.Code));
        captured.Status.Should().Be(303);
        cookies.Apply(captured);
        var confirmation = await native.RequestAsync(host, "/invitations/confirm", cookies.Header);
        confirmation.Status.Should().Be(200);
        cookies.Apply(confirmation);
        if (registration)
        {
            var started = await native.RequestAsync(host, "/api/auth/signin/keycloak", cookies.Header, "POST",
                Form("csrfToken", confirmation.Data.GetProperty("signinCsrfToken").GetString()!) + "&returnTo=%2F&invitation=continue");
            started.Status.Should().Be(303);
            cookies.Apply(started);
            var callback = await browser.CompleteBffRegistrationAsync(PrivateLocation(started), email, KeycloakFixture.DemoPassword);
            var completed = await native.RequestAsync(host, callback.PathAndQuery, cookies.Header);
            completed.Status.Should().Be(303);
            RequireCookieAbsent(completed.Cookies, value => value.StartsWith("__Host-ls-session=", StringComparison.Ordinal));
            cookies.Apply(completed);
            confirmation = await native.RequestAsync(host, "/invitations/confirm", cookies.Header);
        }
        var proof = confirmation.Data.GetProperty("csrfToken").GetString()!;
        var checkedOffer = await native.RequestAsync(host, "/api/auth/invitation/inspect", cookies.Header, "POST", Form("csrfToken", proof));
        if (registration)
        {
            checkedOffer.Data.GetProperty("state").GetString().Should().Be("bootstrap");
            var created = await native.RequestAsync(host, "/api/auth/entry/create", cookies.Header, "POST",
                Form("csrfToken", checkedOffer.Data.GetProperty("entryCsrfToken").GetString()!));
            created.Status.Should().Be(303);
            RequireCookieAbsent(created.Cookies, value => value.StartsWith("__Host-ls-session=", StringComparison.Ordinal));
            cookies.Apply(created);
            checkedOffer = await native.RequestAsync(host, "/api/auth/invitation/inspect", cookies.Header, "POST", Form("csrfToken", proof));
        }
        checkedOffer.Data.GetProperty("state").GetString().Should().Be("forbidden", "the fresh unverified provider email cannot inspect an offer");
        checkedOffer.PrivateFree.Should().BeTrue();
        var userBefore = await administrator.UserAsync(email);
        userBefore.GetProperty("emailVerified").GetBoolean().Should().BeFalse();
        (!userBefore.TryGetProperty("attributes", out var attributes) || !attributes.TryGetProperty("user_id", out _))
            .Should().BeTrue("a nonseed account receives no compatibility actor identity");
        var verified = await native.RequestAsync(host, "/api/auth/invitation/verify", cookies.Header, "POST", Form("csrfToken", proof));
        verified.Status.Should().Be(303);
        cookies.Apply(verified);
        using var verificationBudget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var verifiedCallback = await browser.CompleteBffEmailVerificationAsync(PrivateLocation(verified),
            cancellation => provider.ReadInvitationMailAsync(email, "/realms/learnstack/login-actions/action-token", cancellation), verificationBudget.Token);
        var actionResult = QueryHelpers.ParseQuery(verifiedCallback.Query);
        if (actionResult.GetValueOrDefault("kc_action") != "VERIFY_EMAIL" || actionResult.GetValueOrDefault("kc_action_status") != "success")
            throw new InvalidOperationException("Invitation provider required-action result was not the exact VERIFY_EMAIL/success tuple.");
        output.WriteLine("Invitation provider required-action result: VERIFY_EMAIL/success; verification used the delivered SMTP link.");
        var verificationReturned = await native.RequestAsync(host, verifiedCallback.PathAndQuery, cookies.Header);
        if (verificationReturned.Status != 303)
        {
            var fields = QueryHelpers.ParseQuery(verifiedCallback.Query);
            var keys = string.Join(',', fields.Keys.Select(key => key is "state" or "code" or "iss" or "session_state" or "kc_action" or "kc_action_status"
                or "error" or "error_description" or "error_uri" ? key : "other").Order(StringComparer.Ordinal));
            var hasProof = cookies.Header.Split("; ").Any(pair => pair.StartsWith("__Host-ls-login-" + fields.GetValueOrDefault("state") + "=", StringComparison.Ordinal));
            var responseCode = verificationReturned.Data.ValueKind == JsonValueKind.Object && verificationReturned.Data.TryGetProperty("code", out var code)
                ? code.GetString() : null;
            var closedCode = responseCode is "invalid_request" or "transaction" or "revoked" or "expired" or "response" or "unavailable"
                ? responseCode : "other";
            throw new InvalidOperationException("Invitation verification callback refused: code=" + closedCode + "; fields=" + keys
                + "; unique=" + fields.All(field => field.Value.Count == 1) + "; proof=" + hasProof + ".");
        }
        verificationReturned.Status.Should().Be(303);
        RequireCookieAbsent(verificationReturned.Cookies, value => value.StartsWith("__Host-ls-session=", StringComparison.Ordinal));
        cookies.Apply(verificationReturned);
        var userAfter = await administrator.UserAsync(email);
        userAfter.GetProperty("emailVerified").GetBoolean().Should().BeTrue();
        RequirePrivateEqual(userBefore.GetProperty("id").GetString(), userAfter.GetProperty("id").GetString(),
            "following verification email retains the exact provider subject");
        confirmation = await native.RequestAsync(host, "/invitations/confirm", cookies.Header);
        confirmation.Data.GetProperty("state").GetString().Should().Be("inspect", "callback alone never supplies current verified-email evidence");
        proof = confirmation.Data.GetProperty("csrfToken").GetString()!;
        var inspected = await native.RequestAsync(host, "/api/auth/invitation/inspect", cookies.Header, "POST", Form("csrfToken", proof));
        inspected.Status.Should().Be(200);
        inspected.Data.GetProperty("state").GetString().Should().Be("ready");
        var acceptance = await native.RequestAsync(host, "/api/auth/invitation/accept", cookies.Header, "POST",
            Form("csrfToken", proof) + "&" + Form("version", inspected.Data.GetProperty("version").GetString()!));
        acceptance.Status.Should().Be(200);
        acceptance.Data.GetProperty("state").GetString().Should().Be("accepted");
        acceptance.Data.GetProperty("disposition").GetString().Should().Be("created");
        acceptance.Data.GetProperty("sessionAvailable").GetBoolean().Should().BeTrue();
        cookies.Apply(acceptance);
        var session = await native.RequestAsync(host, "/api/auth/session", cookies.Header);
        session.Data.GetProperty("state").GetString().Should().Be("authenticated");
        var detail = await InvitationAdminRequestAsync(api, tenant, credential.AccessToken, secret, nativeSecret,
            HttpMethod.Get, "/api/v1/identity/invitations/" + mail.InvitationId);
        using (detail)
        {
            detail.StatusCode.Should().Be(HttpStatusCode.OK);
            using var body = await JsonDocument.ParseAsync(await detail.Content.ReadAsStreamAsync());
            body.RootElement.GetProperty("acceptanceDisposition").GetString().Should().Be("created");
            body.RootElement.TryGetProperty("membershipId", out _).Should().BeFalse();
        }
        if (!registration)
        {
            var originalSessionProof = session.Data.GetProperty("csrfToken").GetString();
            // A second offer reaches the same already-active membership. Neither party
            // may describe a second grant, and the ordinary live session stays intact.
            var second = await CreateAndDeliverInvitationAsync(database, api, provider, tenant, credential.AccessToken, secret, nativeSecret, email);
            var cancel = await native.RequestAsync(host, "/api/auth/invitation/cancel", cookies.Header, "POST", Form("csrfToken", proof));
            cancel.Status.Should().Be(303);
            cookies.Apply(cancel);
            landing = await native.RequestAsync(host, "/invitations", cookies.Header);
            cookies.Apply(landing);
            captured = await native.RequestAsync(host, "/api/auth/invitation/capture", cookies.Header, "POST",
                Form("csrfToken", landing.Data.GetProperty("csrfToken").GetString()!) + "&" + Form("code", second.Code));
            captured.Status.Should().Be(303);
            cookies.Apply(captured);
            confirmation = await native.RequestAsync(host, "/invitations/confirm", cookies.Header);
            proof = confirmation.Data.GetProperty("csrfToken").GetString()!;
            inspected = await native.RequestAsync(host, "/api/auth/invitation/inspect", cookies.Header, "POST", Form("csrfToken", proof));
            inspected.Data.GetProperty("state").GetString().Should().Be("ready");
            acceptance = await native.RequestAsync(host, "/api/auth/invitation/accept", cookies.Header, "POST",
                Form("csrfToken", proof) + "&" + Form("version", inspected.Data.GetProperty("version").GetString()!));
            acceptance.Data.GetProperty("disposition").GetString().Should().Be("already_member");
            acceptance.Data.GetProperty("sessionAvailable").GetBoolean().Should().BeTrue();
            RequireCookieAbsent(acceptance.Cookies, value => value.StartsWith("__Host-ls-session=", StringComparison.Ordinal));
            cookies.Apply(acceptance);
            session = await native.RequestAsync(host, "/api/auth/session", cookies.Header);
            RequirePrivateEqual(originalSessionProof, session.Data.GetProperty("csrfToken").GetString(), "already-member acceptance preserves the live session");
            using var secondDetail = await InvitationAdminRequestAsync(api, tenant, credential.AccessToken, secret, nativeSecret,
                HttpMethod.Get, "/api/v1/identity/invitations/" + second.InvitationId);
            secondDetail.StatusCode.Should().Be(HttpStatusCode.OK);
            using var secondBody = await JsonDocument.ParseAsync(await secondDetail.Content.ReadAsStreamAsync());
            secondBody.RootElement.GetProperty("acceptanceDisposition").GetString().Should().Be("already_member");

            var otherEmail = "other-recipient-" + Guid.NewGuid().ToString("N") + "@example.test";
            var wrongOffer = await CreateAndDeliverInvitationAsync(database, api, provider, tenant, credential.AccessToken, secret, nativeSecret, otherEmail);
            cancel = await native.RequestAsync(host, "/api/auth/invitation/cancel", cookies.Header, "POST", Form("csrfToken", proof));
            cancel.Status.Should().Be(303);
            cookies.Apply(cancel);
            landing = await native.RequestAsync(host, "/invitations", cookies.Header);
            cookies.Apply(landing);
            captured = await native.RequestAsync(host, "/api/auth/invitation/capture", cookies.Header, "POST",
                Form("csrfToken", landing.Data.GetProperty("csrfToken").GetString()!) + "&" + Form("code", wrongOffer.Code));
            captured.Status.Should().Be(303);
            cookies.Apply(captured);
            confirmation = await native.RequestAsync(host, "/invitations/confirm", cookies.Header);
            proof = confirmation.Data.GetProperty("csrfToken").GetString()!;
            var refused = await native.RequestAsync(host, "/api/auth/invitation/inspect", cookies.Header, "POST", Form("csrfToken", proof));
            refused.Data.GetProperty("state").GetString().Should().Be("forbidden");
            refused.Data.GetProperty("hasRecipientAddress").GetBoolean().Should().BeFalse();
            refused.PrivateFree.Should().BeTrue();
            RequireCookieAbsent(refused.Cookies, value => value.StartsWith("__Host-ls-session=", StringComparison.Ordinal));
            cookies.Apply(refused);
            var retained = await native.RequestAsync(host, "/api/auth/session", cookies.Header);
            retained.Data.GetProperty("state").GetString().Should().Be("authenticated");
            RequirePrivateEqual(originalSessionProof, retained.Data.GetProperty("csrfToken").GetString(), "a mismatched recipient cannot replace the live account");
            using var wrongDetail = await InvitationAdminRequestAsync(api, tenant, credential.AccessToken, secret, nativeSecret,
                HttpMethod.Get, "/api/v1/identity/invitations/" + wrongOffer.InvitationId);
            wrongDetail.StatusCode.Should().Be(HttpStatusCode.OK);
            using var wrongBody = await JsonDocument.ParseAsync(await wrongDetail.Content.ReadAsStreamAsync());
            wrongBody.RootElement.GetProperty("acceptanceDisposition").ValueKind.Should().Be(JsonValueKind.Null);
        }
        await native.CompleteAsync();
    }

    private static async Task CreateExistingInvitationIdentityAsync(BffNativeDriver native, KeycloakBrowserFlow browser,
        string host, BffWireCookies cookies, string email)
    {
        var csrf = await native.RequestAsync(host, "/api/auth/csrf", cookies.Header);
        cookies.Apply(csrf);
        var start = await native.RequestAsync(host, "/api/auth/signin/keycloak", cookies.Header, "POST", Form("csrfToken", csrf.Data.GetProperty("csrfToken").GetString()!));
        start.Status.Should().Be(303);
        cookies.Apply(start);
        var callback = await browser.CompleteBffLoginAsync(PrivateLocation(start), email, KeycloakFixture.DemoPassword);
        var complete = await native.RequestAsync(host, callback.PathAndQuery, cookies.Header);
        complete.Status.Should().Be(303);
        cookies.Apply(complete);
        var entry = await native.RequestAsync(host, "/api/auth/entry", cookies.Header);
        entry.Data.GetProperty("identityState").GetString().Should().Be("missing");
        var created = await native.RequestAsync(host, "/api/auth/entry/create", cookies.Header, "POST", Form("csrfToken", entry.Data.GetProperty("csrfToken").GetString()!));
        created.Status.Should().Be(303);
        RequireCookieAbsent(created.Cookies, value => value.StartsWith("__Host-ls-session=", StringComparison.Ordinal));
        cookies.Apply(created);
        var active = await native.RequestAsync(host, "/api/auth/entry", cookies.Header);
        active.Data.GetProperty("identityState").GetString().Should().Be("active");
        active.Data.GetProperty("contexts").GetProperty("items").GetArrayLength().Should().Be(0);
    }

    private static async Task<(string InvitationId, string Code)> CreateAndDeliverInvitationAsync(IdentityPublicReadFixture database,
        ApiHost api, KeycloakFixture provider, SeedTenant tenant, string credential, string secret, string nativeSecret, string email)
    {
        using var roles = await InvitationAdminRequestAsync(api, tenant, credential, secret, nativeSecret, HttpMethod.Get, "/api/v1/identity/invitable-roles");
        roles.StatusCode.Should().Be(HttpStatusCode.OK);
        using var roleBody = await JsonDocument.ParseAsync(await roles.Content.ReadAsStreamAsync());
        var role = roleBody.RootElement.GetProperty("items").EnumerateArray().Single(value => value.GetProperty("builtInKey").GetString() == "Learner"
            && value.GetProperty("capabilityScope").GetString() == "Tenant");
        using var created = await InvitationAdminRequestAsync(api, tenant, credential, secret, nativeSecret, HttpMethod.Post, "/api/v1/identity/invitations",
            new { email, roleId = role.GetProperty("roleId").GetString(), roleVersion = role.GetProperty("version").GetString() });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var path = created.Headers.Location?.OriginalString;
        if (path is null || !Guid.TryParseExact(path.Split('/').Last(), "D", out var id))
            throw new InvalidOperationException("Invitation creation lacks its exact result location.");
        BackgroundJobId job;
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString))
        {
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await using var transaction = await app.BeginTransactionAsync();
            await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant.TenantId.Value, null);
            await using var read = new NpgsqlCommand("SELECT current_delivery_job_id FROM invitations WHERE tenant_id=@tenant AND id=@id", app, transaction);
            read.Parameters.AddWithValue("tenant", tenant.TenantId.Value);
            read.Parameters.AddWithValue("id", id);
            job = BackgroundJobId.From((Guid)(await read.ExecuteScalarAsync())!);
        }
        // The construction fixture has no dispatcher. Exercise its actual queued job with
        // the established confined test routing source, not a direct SMTP call or fake link.
        var executor = api.Factory.Services.GetRequiredService<JobExecutor>();
        var start = await executor.ExecuteAsync(await InvitationRoutingClaim.ClaimAsync(database.OutboxConnectionString, job), BackgroundJobTestRun.Create());
        start.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        var receipt = start.Receipt!;
        (await executor.ExecuteBusinessAsync(receipt, receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Prepare))).CommitAcknowledged.Should().BeTrue();
        var state = await executor.ReadExternalStateAsync(receipt, CancellationToken.None);
        state.Handle.Should().NotBeNull();
        var arm = await executor.RecordExternalArmAsync(receipt, state.Handle!, receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Arm), CancellationToken.None);
        arm.CommitAcknowledged.Should().BeTrue();
        var sent = await executor.ExecuteExternalAsync(state.Handle!, arm, CancellationToken.None);
        sent.Status.Should().Be(JobExternalExecutionStatus.Reported);
        sent.ProviderIssued.Should().BeTrue();
        sent.CleanupFailed.Should().BeFalse();
        sent.Observation!.State.Should().Be(BackgroundJobExternalState.Succeeded);
        (await executor.RecordExternalOutcomeAsync(receipt, state.Handle!, receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Outcome), sent.Observation, CancellationToken.None)).CommitAcknowledged.Should().BeTrue();
        var final = await executor.ExecuteAsync(await InvitationRoutingClaim.ClaimAsync(database.OutboxConnectionString, job), BackgroundJobTestRun.Create());
        (await executor.ExecuteBusinessAsync(final.Receipt!, final.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Finalize))).CommitAcknowledged.Should().BeTrue();
        var delivered = await provider.ReadInvitationMailAsync(email, "/invitations", CancellationToken.None);
        var link = new Uri(delivered);
        if (link.GetLeftPart(UriPartial.Authority) != "https://" + tenant.Host + ":3000" || link.Query.Length != 0 || link.Fragment.Length != 81)
            throw new InvalidOperationException("Invitation email lacks its exact native fragment landing.");
        return (id.ToString("D"), link.Fragment[1..]);
    }

    private static async Task<HttpResponseMessage> InvitationAdminRequestAsync(ApiHost api, SeedTenant tenant, string credential,
        string secret, string nativeSecret, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(api.Origin + path));
        request.Headers.Authorization = new("Bearer", credential);
        request.Headers.Add("X-LearnStack-Host", Host(tenant));
        request.Headers.Add("X-LearnStack-Hop-Secret", secret);
        request.Headers.Add("X-LearnStack-Native-Session-Secret", nativeSecret);
        request.Headers.Add("X-LearnStack-Visitor-Address", "198.51.100.25");
        request.Headers.Add("X-LearnStack-Native-Tenant-Id", tenant.TenantId.Value.ToString("D"));
        if (body is not null)
        {
            request.Headers.Add("Idempotency-Key", Guid.CreateVersion7().ToString("D"));
            request.Content = JsonContent.Create(body);
        }
        return await api.Client.SendAsync(request);
    }

    private sealed class InvitationRoutingClaim : BackgroundJobRoutingClaim.MintingSource
    {
        internal static async Task<BackgroundJobRoutingClaim> ClaimAsync(string connectionString, BackgroundJobId job)
        {
            await using var dispatcher = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
            await using var command = new NpgsqlCommand("""
                UPDATE background_jobs SET ownership_token=@token,ownership_kind='routing',ownership_until=clock_timestamp()+interval '4 minutes'
                WHERE id=@job RETURNING id,tenant_id,organization_id,definition_key,queue,wake_generation,ownership_token,
                    ownership_until,original_due_at,profile,phase,clock_timestamp()
                """, dispatcher);
            command.Parameters.AddWithValue("job", job.Value);
            command.Parameters.AddWithValue("token", Guid.NewGuid());
            await using var row = await command.ExecuteReaderAsync();
            (await row.ReadAsync()).Should().BeTrue();
            return MintClaim(row);
        }
    }
}
