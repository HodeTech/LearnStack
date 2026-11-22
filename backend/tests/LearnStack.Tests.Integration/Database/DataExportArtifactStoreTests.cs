using FluentAssertions;
using LearnStack.Infrastructure.DataProtection;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Time;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0107 § 4: <see cref="PostgresDataExportArtifactStore"/> as <c>learnstack_app</c> on the ambient group
/// transaction of the seeded schema — sealed chunks that read back, the group bound, revocation that leaves no
/// ciphertext, and the database guards behind it. Every case rolls back.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class DataExportArtifactStoreTests(SchemaFixture schema)
{
    private const int Size = (2 * DataRequestLimits.ChunkPlaintextBytes) + 77;

    [Theory]
    [InlineData("tenant")]
    [InlineData("organization")]
    public async Task A_put_bundle_reads_back_chunk_by_chunk_and_a_revocation_leaves_no_ciphertext(string group)
    {
        var (tenant, organization, subject, membership) = group == "tenant"
            ? (SchemaFixture.TenantA, (Guid?)null, AdminA, AdminMembershipA)
            : (SchemaFixture.TenantA, SchemaFixture.OrgA1, PersonA, BranchMembershipA1);
        await using var scope = await DataExportTestKit.OpenAsync(schema.Postgres.AppConnectionString, tenant, organization, [subject]);
        using var cipher = DataExportTestKit.Cipher();
        var store = Store(scope, cipher);
        if (organization is not null)
        {
            // The seeded subject already holds an available artifact in this group: withdraw it first, as the export does.
            (await store.PurgeAsync(new DataExportArtifactLocator(BranchA1.Artifact!.Value, new DataRequestScopeGroup(
                    TenantId.From(tenant), OrganizationId.From(organization.Value))), DataExportArtifactStatus.Revoked, CancellationToken.None))
                .Should().Be(DataExportArtifactStatus.Revoked);
        }

        var request = await FulfillingRequestAsync(scope, subject, membership);
        var bundle = Bundle(Size);
        var artifactId = Guid.CreateVersion7();

        var put = await store.PutAsync(new DataExportArtifactPut(artifactId, request.Id.Value, request.SubjectUserId, request.Group,
            Size, [new DataExportTruncation("audit.entries", 3, 9)]), Chunks(bundle), CancellationToken.None);

        put.Status.Should().Be(DataExportArtifactStatus.Available);
        put.ChunkCount.Should().Be(3);
        put.KeyVersion.Should().Be(cipher.ActiveKeyVersion);
        put.IsReadable.Should().BeTrue();
        put.Truncations.Should().Equal(new DataExportTruncation("audit.entries", 3, 9));
        var locator = new DataExportArtifactLocator(artifactId, request.Group);
        var metadata = (await store.ReadMetadataAsync(locator, CancellationToken.None))!;
        metadata.IsReadable.Should().BeTrue();
        metadata.ByteCount.Should().Be(Size);
        metadata.Truncations.Should().Equal(put.Truncations);
        var read = new List<byte>();
        for (var index = 0; index < 3; index++)
        {
            var chunk = (await store.ReadChunkAsync(locator, index, CancellationToken.None))!;
            chunk.Index.Should().Be(index);
            chunk.Count.Should().Be(3);
            read.AddRange(chunk.Bytes.ToArray());
        }

        read.Should().Equal(bundle);
        (await store.ReadChunkAsync(locator, 3, CancellationToken.None)).Should().BeNull();
        (await store.ReadChunkAsync(locator, -1, CancellationToken.None)).Should().BeNull();
        (await ScalarAsync<long>(scope.Connection, scope.Transaction,
            "SELECT count(*) FROM public.data_export_chunks WHERE artifact_id=@id AND position(@probe in ciphertext) > 0",
            ("id", artifactId), ("probe", bundle[..32]))).Should().Be(0, "no stored chunk holds the plaintext");
        await ExecuteAsync(scope.Connection, scope.Transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        await ExecuteAsync(scope.Connection, scope.Transaction, "SET CONSTRAINTS ALL DEFERRED");

        (await store.PurgeAsync(locator, DataExportArtifactStatus.Purged, CancellationToken.None))
            .Should().Be(DataExportArtifactStatus.Available, "a purge before the expiry changes nothing");
        (await store.PurgeAsync(locator, DataExportArtifactStatus.Revoked, CancellationToken.None))
            .Should().Be(DataExportArtifactStatus.Revoked);
        (await store.PurgeAsync(locator, DataExportArtifactStatus.Revoked, CancellationToken.None))
            .Should().Be(DataExportArtifactStatus.Revoked, "repeating a revocation changes nothing");
        (await store.ReadChunkAsync(locator, 0, CancellationToken.None)).Should().BeNull();
        var revoked = (await store.ReadMetadataAsync(locator, CancellationToken.None))!;
        revoked.Status.Should().Be(DataExportArtifactStatus.Revoked);
        revoked.IsReadable.Should().BeFalse();
        revoked.PurgedAt.Should().NotBeNull();
        (await ScalarAsync<long>(scope.Connection, scope.Transaction,
            "SELECT count(*) FROM public.data_export_chunks WHERE artifact_id=@id", ("id", artifactId))).Should().Be(0);
        await ExecuteAsync(scope.Connection, scope.Transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        var refusal = await RefuseAsync(scope.Transaction, () => InsertChunkAsync(scope.Connection, scope.Transaction, tenant,
            organization, artifactId, 0));
        refusal.MessageText.Should().Be("Data export chunk refused.", "a revoked artifact takes no chunk again");
        await scope.Frame.FailAsync();
    }

    [Theory]
    [InlineData("short_middle")]
    [InlineData("long_last")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("other_request")]
    public async Task A_put_that_does_not_match_its_declared_bundle_is_refused(string shape)
    {
        await using var scope = await DataExportTestKit.OpenAsync(schema.Postgres.AppConnectionString, SchemaFixture.TenantA, null, [AdminA]);
        using var cipher = DataExportTestKit.Cipher();
        var store = Store(scope, cipher);
        var request = await FulfillingRequestAsync(scope, AdminA, AdminMembershipA);
        var bundle = Bundle(Size);
        var chunks = shape switch
        {
            "short_middle" => Pieces(bundle[..DataRequestLimits.ChunkPlaintextBytes], bundle[(DataRequestLimits.ChunkPlaintextBytes + 1)..]),
            "long_last" => Pieces(bundle[..DataRequestLimits.ChunkPlaintextBytes], bundle[DataRequestLimits.ChunkPlaintextBytes..(2 * DataRequestLimits.ChunkPlaintextBytes)],
                [.. bundle[(2 * DataRequestLimits.ChunkPlaintextBytes)..], 1]),
            "missing" => Pieces(bundle[..DataRequestLimits.ChunkPlaintextBytes]),
            "extra" => Pieces([.. Chunked(bundle), new byte[] { 1 }]),
            _ => Pieces(Chunked(bundle)),
        };
        var put = new DataExportArtifactPut(Guid.CreateVersion7(), shape == "other_request" ? Guid.CreateVersion7() : request.Id.Value,
            request.SubjectUserId, request.Group, Size);

        var act = () => store.PutAsync(put, chunks, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await scope.Frame.FailAsync();
    }

    [Fact]
    public async Task An_artifact_is_reachable_only_from_its_own_group()
    {
        // Committed rows: a disposable copy of the seeded schema, so the shared one keeps its census.
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        Guid artifactId;
        await using (var scope = await DataExportTestKit.OpenAsync(database.AppConnectionString, SchemaFixture.TenantA, null, [AdminA]))
        {
            using var cipher = DataExportTestKit.Cipher();
            var store = Store(scope, cipher);
            var request = await FulfillingRequestAsync(scope, AdminA, AdminMembershipA);
            artifactId = Guid.CreateVersion7();
            await store.PutAsync(new DataExportArtifactPut(artifactId, request.Id.Value, request.SubjectUserId, request.Group, 10),
                Pieces(Bundle(10)), CancellationToken.None);
            var foreign = new DataExportArtifactLocator(artifactId, new DataRequestScopeGroup(TenantId.From(SchemaFixture.TenantA),
                OrganizationId.From(SchemaFixture.OrgA1)));
            await FluentActions.Awaiting(() => store.ReadMetadataAsync(foreign, CancellationToken.None))
                .Should().ThrowAsync<InvalidOperationException>("a locator names the announced group or nothing");
            await scope.Frame.CompleteAsync();
        }

        foreach (var (tenant, organization) in new (Guid, Guid?)[]
                 { (SchemaFixture.TenantB, null), (SchemaFixture.TenantA, SchemaFixture.OrgA1) })
        {
            await using var other = await DataExportTestKit.OpenAsync(database.AppConnectionString, tenant, organization);
            using var cipher = DataExportTestKit.Cipher();
            var store = Store(other, cipher);
            var locator = new DataExportArtifactLocator(artifactId, new DataRequestScopeGroup(TenantId.From(tenant),
                organization is { } org ? OrganizationId.From(org) : null));
            (await store.ReadMetadataAsync(locator, CancellationToken.None)).Should().BeNull();
            (await store.ReadChunkAsync(locator, 0, CancellationToken.None)).Should().BeNull();
            (await store.PurgeAsync(locator, DataExportArtifactStatus.Revoked, CancellationToken.None)).Should().BeNull();
            await other.Frame.FailAsync();
        }
    }

    internal static PostgresDataExportArtifactStore Store(DataExportTestKit.GroupScope scope, DataExportCipher cipher) =>
        new(scope.Db, scope.Unit, scope.Context, cipher, new SystemClock(), new DataExportTestKit.NoAuditSubject());

    /// <summary>A fresh export of the subject in the scope's group, saved through the aggregate and fulfilling.</summary>
    private static async Task<DataSubjectRequest> FulfillingRequestAsync(DataExportTestKit.GroupScope scope, Guid subject, Guid membership)
    {
        var now = DateTimeOffset.UtcNow;
        var request = DataSubjectRequest.FileForSelf(DataSubjectRequestId.From(Guid.CreateVersion7()), DataRequestKind.Export,
            UserId.From(subject), new DataRequestScopeGroup(scope.Context.TenantId, scope.Context.OrganizationId),
            [MembershipId.From(membership)], now).Value!;
        scope.Db.Add(request);
        await scope.Db.SaveChangesAsync();
        request.StartFulfilling(now, UserId.SystemActor).IsSuccess.Should().BeTrue();
        await scope.Db.SaveChangesAsync();
        return request;
    }

    private static byte[] Bundle(int length) => Enumerable.Range(0, length).Select(index => (byte)('a' + (index % 26))).ToArray();

    private static byte[][] Chunked(byte[] bundle) =>
        bundle.Chunk(DataRequestLimits.ChunkPlaintextBytes).ToArray();

    private static IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks(byte[] bundle) => Pieces(Chunked(bundle));

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Pieces(params byte[][] pieces)
    {
        foreach (var piece in pieces)
        {
            await Task.Yield();
            yield return piece;
        }
    }

    private static async Task<PostgresException> RefuseAsync(NpgsqlTransaction transaction, Func<Task> act)
    {
        await transaction.SaveAsync("probe");
        var refusal = (await act.Should().ThrowAsync<PostgresException>()).Which;
        await transaction.RollbackAsync("probe");
        return refusal;
    }
}
