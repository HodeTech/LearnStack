using FluentAssertions;
using LearnStack.Infrastructure.DataProtection;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataExportTestKit;
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
        (await ScalarAsync<bool>(scope.Connection, scope.Transaction,
            "SELECT abs(extract(epoch FROM created_at - clock_timestamp())) < 60 FROM public.data_export_artifacts WHERE id=@id",
            ("id", artifactId))).Should().BeTrue("the artifact is stamped by the database clock");
        await CompleteAsync(scope, request);
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
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(schema.Postgres);
        Guid artifactId;
        await using (var scope = await DataExportTestKit.OpenAsync(database.AppConnectionString, SchemaFixture.TenantA, null, [AdminA]))
        {
            using var cipher = DataExportTestKit.Cipher();
            var store = Store(scope, cipher);
            var request = await FulfillingRequestAsync(scope, AdminA, AdminMembershipA);
            artifactId = Guid.CreateVersion7();
            await store.PutAsync(new DataExportArtifactPut(artifactId, request.Id.Value, request.SubjectUserId, request.Group, 10),
                Pieces(Bundle(10)), CancellationToken.None);
            await CompleteAsync(scope, request);
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

    /// <summary>
    /// ADR-0107 § 4's associated data binds each chunk to its index and artifact: a chunk spliced into another position or
    /// another artifact, and a chunk whose key left the ring, fail closed as an absence and are logged with identifiers
    /// only, never a key, the ciphertext or the plaintext: critical the first time an artifact is refused under a key
    /// label in the process, a warning for every repeat, so one lost key does not page once per chunk.
    /// </summary>
    [Fact]
    public async Task A_spliced_or_unopenable_chunk_fails_closed_as_an_absence_and_is_logged_critical_once_with_identifiers_only()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(schema.Postgres);
        var bundle = Bundle(Size);
        var artifactId = Guid.CreateVersion7();
        await using (var scope = await DataExportTestKit.OpenAsync(database.AppConnectionString, SchemaFixture.TenantA, null, [AdminA]))
        {
            using var cipher = DataExportTestKit.Cipher();
            var request = await FulfillingRequestAsync(scope, AdminA, AdminMembershipA);
            await Store(scope, cipher).PutAsync(new DataExportArtifactPut(artifactId, request.Id.Value, request.SubjectUserId, request.Group,
                Size), Chunks(bundle), CancellationToken.None);
            await CompleteAsync(scope, request);
            await scope.Frame.CompleteAsync();
        }

        // As the owner with the chunk guard paused: chunk 0's sealed bytes into index 1, and into another artifact's chunk 0.
        var other = TenantWideA.Artifact!.Value;
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString))
        await using (var transaction = await owner.BeginTransactionAsync())
        {
            await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_export_chunks DISABLE TRIGGER tg_data_export_chunks_mutation_guard");
            await ScopeAsync(owner, transaction, SchemaFixture.TenantA);
            await ExecuteAsync(owner, transaction, """
                UPDATE public.data_export_chunks AS target SET nonce=source.nonce, ciphertext=source.ciphertext, tag=source.tag
                FROM public.data_export_chunks AS source
                WHERE source.artifact_id=@artifact AND source.chunk_index=0
                  AND ((target.artifact_id=@artifact AND target.chunk_index=1) OR (target.artifact_id=@other AND target.chunk_index=0))
                """, ("artifact", artifactId), ("other", other));
            await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_export_chunks ENABLE TRIGGER tg_data_export_chunks_mutation_guard");
            await transaction.CommitAsync();
        }

        await using (var scope = await DataExportTestKit.OpenAsync(database.AppConnectionString, SchemaFixture.TenantA, null))
        {
            using var cipher = DataExportTestKit.Cipher();
            var logs = new CapturingLogger();
            var refusals = new DataExportCustodyRefusals();
            var store = Store(scope, cipher, logs, refusals);
            var group = new DataRequestScopeGroup(TenantId.From(SchemaFixture.TenantA), null);
            var first = (await store.ReadChunkAsync(new DataExportArtifactLocator(artifactId, group), 0, CancellationToken.None))!;
            first.Bytes.ToArray().Should().Equal(bundle[..DataRequestLimits.ChunkPlaintextBytes], "the untouched chunk still opens");
            (await store.ReadChunkAsync(new DataExportArtifactLocator(artifactId, group), 1, CancellationToken.None))
                .Should().BeNull("a chunk moved to another index fails authentication");
            (await store.ReadChunkAsync(new DataExportArtifactLocator(other, group), 0, CancellationToken.None))
                .Should().BeNull("a chunk moved into another artifact fails authentication");
            (await store.ReadChunkAsync(new DataExportArtifactLocator(artifactId, group), 2, CancellationToken.None))
                .Should().NotBeNull("the refusals left the transaction usable");

            // A ring that no longer holds the artifact's key: no mint is offered and a read is the same absence.
            using var rotated = DataExportTestKit.Cipher("{\"active\":\"next\",\"keys\":{\"next\":\""
                + Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()) + "\"}}");
            var retired = Store(scope, rotated, logs, refusals);
            retired.CanOpen(cipher.ActiveKeyVersion).Should().BeFalse();
            store.CanOpen(cipher.ActiveKeyVersion).Should().BeTrue();
            (await retired.ReadChunkAsync(new DataExportArtifactLocator(artifactId, group), 0, CancellationToken.None)).Should().BeNull();
            (await retired.ReadChunkAsync(new DataExportArtifactLocator(artifactId, group), 2, CancellationToken.None)).Should().BeNull();
            scope.Unit.IsRollbackOnly.Should().BeFalse("a custody refusal is not a storage failure");
            await scope.Frame.FailAsync();

            logs.Entries.Should().HaveCount(4).And.OnlyContain(entry => entry.EventId.Id == 3193
                && entry.EventId.Name == "DataExportChunkCustodyRefused" && entry.Exception == null);
            logs.Entries.Select(entry => entry.Level).Should().Equal([LogLevel.Critical, LogLevel.Critical, LogLevel.Warning, LogLevel.Warning],
                "the first refusal of each artifact under its key label is critical; the same pair again, by another chunk or "
                + "another store of the process, is a warning");
            var rendered = string.Join('\n', logs.Entries.Select(entry => entry.Message));
            rendered.Should().Contain(artifactId.ToString("D")).And.Contain(other.ToString("D"));
            rendered.Should().NotContain(Convert.ToBase64String(bundle.AsSpan(0, 48)))
                .And.NotContain(System.Text.Encoding.ASCII.GetString(bundle, 0, 48));
            DataExportTestKit.KeyRing.Should().NotBeNullOrEmpty();
            foreach (var key in System.Text.Json.JsonDocument.Parse(DataExportTestKit.KeyRing).RootElement.GetProperty("keys").EnumerateObject())
                rendered.Should().NotContain(key.Value.GetString()!, "no key material reaches a log");
        }
    }

    internal static PostgresDataExportArtifactStore Store(DataExportTestKit.GroupScope scope, DataExportCipher cipher,
        ILogger<PostgresDataExportArtifactStore>? logger = null, DataExportCustodyRefusals? refusals = null) =>
        new(scope.Db, scope.Unit, scope.Context, cipher, new DataExportTestKit.NoAuditSubject(), refusals ?? new DataExportCustodyRefusals(),
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresDataExportArtifactStore>.Instance);

    /// <summary>Completes a fulfilling export on the scope's transaction, as the job does before it commits.</summary>
    internal static async Task CompleteAsync(DataExportTestKit.GroupScope scope, DataSubjectRequest request)
    {
        request.Complete([new(RetainedReferenceKind.RequestReceipt, 1)], DateTimeOffset.UtcNow, UserId.SystemActor).IsSuccess.Should().BeTrue();
        await scope.Db.SaveChangesAsync();
    }

    /// <summary>A fresh export of the subject in the scope's group, saved through the aggregate and fulfilling.</summary>
    internal static async Task<DataSubjectRequest> FulfillingRequestAsync(DataExportTestKit.GroupScope scope, Guid subject, Guid membership)
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

    internal static byte[] Bundle(int length) => Enumerable.Range(0, length).Select(index => (byte)('a' + (index % 26))).ToArray();

    private static byte[][] Chunked(byte[] bundle) =>
        bundle.Chunk(DataRequestLimits.ChunkPlaintextBytes).ToArray();

    internal static IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks(byte[] bundle) => Pieces(Chunked(bundle));

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Pieces(params byte[][] pieces)
    {
        foreach (var piece in pieces)
        {
            await Task.Yield();
            yield return piece;
        }
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

    private sealed class CapturingLogger : ILogger<PostgresDataExportArtifactStore>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add(new(logLevel, eventId, formatter(state, exception), exception));
    }
}
