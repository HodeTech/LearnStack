using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class DatabaseQualificationProofTests(SchemaFixture schema)
{
    [Fact]
    public async Task Proof_factory_itself_refuses_raw_migration_and_platform_sources_without_a_builder_guard()
    {
        foreach (var connection in new[]
        {
            schema.Postgres.MigrationConnectionString,
            schema.Postgres.PlatformConnectionString,
        })
        {
            // Deliberately omit ApplicationDataSource.Build: the proof factory must enforce its own role boundary.
            await using var raw = NpgsqlDataSource.Create(connection);
            await FluentActions.Awaiting(async () =>
            {
                await using var unexpected = await DatabaseQualificationProof.OpenAsync(raw);
            }).Should().ThrowAsync<InvalidOperationException>();
        }
        await using var application = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var positive = await DatabaseQualificationProof.OpenAsync(application);
        await using var source = new SourceLane(schema.Postgres.AppConnectionString, evidence: false);
        await source.QualifyAsync(positive);
        source.IsQualified.Should().BeTrue();
    }

    [Fact]
    public async Task One_live_business_proof_qualifies_both_lanes_and_disposal_releases_its_actual_marker()
    {
        await using var business = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(observer);
        await using var proof = await DatabaseQualificationProof.OpenAsync(business);
        await using var marker = MarkerQuery(observer, proof);
        (await marker.ExecuteScalarAsync()).Should().Be(1L);
        await using var read = new SourceLane(schema.Postgres.AppConnectionString, evidence: false);
        await using var evidence = new SourceLane(schema.Postgres.AppConnectionString, evidence: true);
        await read.QualifyAsync(proof);
        await evidence.QualifyAsync(proof);
        read.IsQualified.Should().BeTrue();
        evidence.IsQualified.Should().BeTrue();
        await proof.DisposeAsync();
        (await marker.ExecuteScalarAsync()).Should().Be(0L, "the transaction marker must not survive pooled connection return");
        // Startup proof closure does not revoke the already-qualified runtime sources.
        read.IsQualified.Should().BeTrue();
        evidence.IsQualified.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Qualification_cannot_upgrade_an_unbound_result_or_retarget_an_existing_proof(bool evidence, bool initiallyBound)
    {
        await using var business = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        await using var first = await DatabaseQualificationProof.OpenAsync(business);
        await using var second = await DatabaseQualificationProof.OpenAsync(business);
        await using var source = new SourceLane(schema.Postgres.AppConnectionString, evidence);
        await source.QualifyAsync(initiallyBound ? first : null);
        source.IsQualified.Should().BeTrue();
        await FluentActions.Awaiting(() => source.QualifyAsync(second)).Should().ThrowAsync<InvalidOperationException>();
        if (initiallyBound)
            await FluentActions.Awaiting(() => source.QualifyAsync(null)).Should().ThrowAsync<InvalidOperationException>();
        await source.QualifyAsync(initiallyBound ? first : null);
    }

    [Theory]
    [InlineData("disposed")]
    [InlineData("canceled")]
    [InlineData("expired")]
    public async Task Closed_or_expired_proof_cannot_qualify_either_lane(string closure)
    {
        await using var business = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        using var cancellation = new CancellationTokenSource();
        await using var proof = await DatabaseQualificationProof.OpenAsync(business, cancellation.Token);
        if (closure == "disposed") await proof.DisposeAsync();
        else if (closure == "canceled") await cancellation.CancelAsync();
        else
        {
            // Observe the production five-second deadline, rather than inventing a shorter test clock.
            await FluentActions.Awaiting(() => Task.Delay(Timeout.InfiniteTimeSpan, proof.DeadlineToken)
                .WaitAsync(TimeSpan.FromSeconds(8))).Should().ThrowAsync<OperationCanceledException>();
            proof.DeadlineToken.IsCancellationRequested.Should().BeTrue();
        }
        foreach (var evidence in new[] { false, true })
        {
            await using var source = new SourceLane(schema.Postgres.AppConnectionString, evidence);
            var qualify = FluentActions.Awaiting(() => source.QualifyAsync(proof));
            if (closure == "disposed") await qualify.Should().ThrowAsync<InvalidOperationException>();
            else await qualify.Should().ThrowAsync<OperationCanceledException>();
            source.IsQualified.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Proof_from_another_database_cannot_qualify_either_lane_even_with_the_same_authority()
    {
        await using var other = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using (var migration = (NpgsqlConnection)await PostgresFixture.OpenAsync(other.MigrationConnectionString))
            await IdentityAuthorityDeploymentInstaller.InstallAsync(migration, IdentityMembershipFixture.Authority);
        await using var business = ApplicationDataSource.Build(other.AppConnectionString);
        await using var proof = await DatabaseQualificationProof.OpenAsync(business);
        foreach (var evidence in new[] { false, true })
        {
            await using var foreign = new SourceLane(schema.Postgres.AppConnectionString, evidence);
            await FluentActions.Awaiting(() => foreign.QualifyAsync(proof)).Should().ThrowAsync<InvalidOperationException>();
            foreign.IsQualified.Should().BeFalse();
            await using var matching = new SourceLane(other.AppConnectionString, evidence);
            await matching.QualifyAsync(proof);
            matching.IsQualified.Should().BeTrue();
        }
    }

    private static NpgsqlCommand MarkerQuery(NpgsqlConnection observer, DatabaseQualificationProof proof)
    {
        var command = new NpgsqlCommand("""
            SELECT count(*) FROM pg_catalog.pg_locks
            WHERE locktype='advisory' AND granted AND pid=@business_pid
                AND classid::bigint=@business_first AND objid::bigint=@business_second AND objsubid=2
            """, observer);
        DatabaseQualificationProof.Bind(command, proof);
        return command;
    }

    private sealed class SourceLane : IAsyncDisposable
    {
        private readonly IdentityAuthorizationReadSource? _read;
        private readonly AuthorizationEvidenceSource? _evidence;

        internal SourceLane(string connection, bool evidence)
        {
            if (evidence) _evidence = new(connection);
            else _read = new(connection);
        }

        internal bool IsQualified => _read?.IsQualified ?? _evidence!.IsQualified;
        internal Task QualifyAsync(DatabaseQualificationProof? proof)
        {
            var authority = IdentityMembershipFixture.Authority;
            if (_read is not null)
                return proof is null ? _read.QualifyAsync(authority.AuthorityId, authority.Issuer)
                    : _read.QualifyAsync(authority.AuthorityId, authority.Issuer, proof);
            return proof is null ? _evidence!.QualifyAsync(authority.AuthorityId, authority.Issuer)
                : _evidence!.QualifyAsync(authority.AuthorityId, authority.Issuer, proof);
        }
        public ValueTask DisposeAsync() => _read?.DisposeAsync() ?? _evidence!.DisposeAsync();
    }
}
