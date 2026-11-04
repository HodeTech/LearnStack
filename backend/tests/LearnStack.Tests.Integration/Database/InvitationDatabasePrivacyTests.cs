using FluentAssertions;
using LearnStack.Infrastructure.Email;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class InvitationDatabasePrivacyTests
{
    [Fact]
    public async Task Actual_server_row_detail_bind_and_sampling_canaries_are_absent_and_dirty_verbosity_is_detected()
    {
        await using var fixture = new PrivacyFixture();
        await fixture.Postgres.InitializeAsync();
        await fixture.Postgres.ExecuteAsSuperuserAsync("""
            CREATE TABLE public.smtp_privacy_probe(email text UNIQUE, verifier text, valid boolean CHECK(valid));
            GRANT INSERT ON public.smtp_privacy_probe TO learnstack_app;
            """);
        var canary = "smtp-proof-" + Guid.NewGuid().ToString("N");
        await using (var app = new NpgsqlConnection(fixture.Postgres.AppConnectionString))
        {
            await app.OpenAsync();
            await using var qualification = new NpgsqlCommand(InvitationEmailDatabasePrivacy.QualificationSql, app);
            (await qualification.ExecuteScalarAsync()).Should().Be(true);
            await CauseFailuresAsync(app, canary);
        }
        (await fixture.Postgres.ServerLogContainsSyntheticCanaryAsync(canary)).Should().BeFalse();
        await fixture.Postgres.ExecuteAsSuperuserAsync("ALTER ROLE learnstack_app SET log_error_verbosity='default'");
        var dirtyCanary = "smtp-proof-" + Guid.NewGuid().ToString("N");
        var fresh = new NpgsqlConnectionStringBuilder(fixture.Postgres.AppConnectionString) { Pooling = false };
        await using (var app = new NpgsqlConnection(fresh.ConnectionString))
        {
            await app.OpenAsync();
            await using var qualification = new NpgsqlCommand(InvitationEmailDatabasePrivacy.QualificationSql, app);
            (await qualification.ExecuteScalarAsync()).Should().Be(false);
            await CauseFailuresAsync(app, dirtyCanary);
        }
        // Positive control: bind logging remains disabled, yet failing-row DETAIL leaks both values.
        (await fixture.Postgres.ServerLogContainsSyntheticCanaryAsync(dirtyCanary)).Should().BeTrue();
    }

    private static async Task CauseFailuresAsync(NpgsqlConnection app, string canary)
    {
        foreach (var valid in new[] { false, true, true })
        {
            await using var command = new NpgsqlCommand("INSERT INTO public.smtp_privacy_probe VALUES(@email,@verifier,@valid)", app);
            command.Parameters.AddWithValue("email", canary + "@example.invalid");
            command.Parameters.AddWithValue("verifier", canary + "-verifier");
            command.Parameters.AddWithValue("valid", valid);
            try { await command.ExecuteNonQueryAsync(); }
            catch (PostgresException failure) when (failure.SqlState is "23514" or "23505") { }
        }
    }
    private sealed class PrivacyFixture : IAsyncDisposable
    {
        internal PostgresFixture Postgres { get; } = new();
        public async ValueTask DisposeAsync() => await Postgres.DisposeAsync();
    }
}
