using System.Text.Json;
using FluentAssertions;
using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Real workflows through the real pipeline and the real store, asserted on what the row
/// <b>means</b> — not merely that it exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second class beside <see cref="AuditPipelineTests"/>.</b> That one proves a
/// seed produces rows. It could not see what the external review of this packet found
/// (ADR-0044 Amendment 6): that <em>replacing</em> a live revision — the second publication
/// of a key, which the seed never performs — failed for both aggregates and rolled back;
/// that a successful row carried no actor and no correlation id, because the seed has
/// neither to carry; and that a taxonomy's bands never reached its row, which a case
/// asserting only the row's existence cannot notice. Every case here drives a command a
/// tenant would send and reads the columns a reader would read.
/// </para>
/// <para>
/// Same composition root, same role, same cleanup discipline as
/// <see cref="AuditPipelineTests"/>: the seeder's graph is a real one, and the rows are
/// written by <c>learnstack_app</c> under the policies and read back as
/// <c>learnstack_platform</c>.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class AuditWorkflowTests : IAsyncLifetime
{
    private static readonly Guid Actor = Guid.Parse("dddddddd-0000-7000-8000-00000000a0a0");
    private const string Correlation = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    private static readonly SeedTenant Tenant = SeedData.English;

    private readonly SchemaFixture _schema;

    public AuditWorkflowTests(SchemaFixture schema) => _schema = schema;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => CleanUpAsync();

    [Fact]
    public async Task Replacing_a_live_content_type_is_one_row_about_the_successor()
    {
        // The Blocker: the publication retires the incumbent and activates the successor on
        // one transaction — two instances of one aggregate — and the composer refused the
        // pair, which rolled back every replacement publication. The handler now designates
        // the successor, and the retirement travels in `changes` under its own pointer.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        await SeedAsync(dataSource);

        var incumbent = Tenant.BuiltInContentTypeId;
        var successor = Guid.CreateVersion7();

        await using (var provider = Compose(dataSource))
        {
            (await SendAsync(provider, new RegisterTenantContentTypeCommand(
                successor,
                BuiltInCustomizations.Card.Key,
                BuiltInCustomizations.SchemaVersion + 1,
                BuiltInCustomizations.Card.DisplayName,
                BuiltInCustomizations.Card.JsonSchema,
                BuiltInCustomizations.Card.RendererKey))).IsSuccess.Should().BeTrue();

            var published = await SendAsync(provider, new PublishTenantContentTypeCommand(successor));
            published.IsSuccess.Should().BeTrue("a replacement publication commits");
        }

        var rows = (await RowsAsync()).Where(row => row.Operation == "customization.content_type.publish").ToList();

        // The FIRST publication — the seed's — and the replacement: one row each.
        rows.Select(row => row.EntityId).Should().BeEquivalentTo(
            [incumbent.ToString(), successor.ToString()]);

        var replacement = rows.Single(row => row.EntityId == successor.ToString());
        replacement.Outcome.Should().Be("success");
        Status(replacement.BeforeState).Should().Be("Draft", "before_state is the successor's");
        Status(replacement.AfterState).Should().Be("Active");

        Change(replacement, $"/TenantContentType/{incumbent}/Status").Should().Be(("\"Active\"", "\"Deprecated\""),
            "the incumbent's retirement is on the record under its own pointer");
        Change(replacement, $"/TenantContentType/{successor}/Status").Should().Be(("\"Draft\"", "\"Active\""));

        // And the actor and trace the request carried, on the row that SUCCEEDED — the row
        // that used to carry neither.
        replacement.ActorUserId.Should().Be(Actor);
        replacement.CorrelationId.Should().Be(Correlation);
    }

    [Fact]
    public async Task Replacing_a_live_taxonomy_is_one_row_about_the_successor()
    {
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        await SeedAsync(dataSource);

        var incumbent = Tenant.BuiltInTaxonomyId;
        var successor = Guid.CreateVersion7();

        await using (var provider = Compose(dataSource))
        {
            (await SendAsync(provider, new RegisterTenantLevelTaxonomyCommand(
                successor,
                BuiltInCustomizations.Plain.Key,
                BuiltInCustomizations.SchemaVersion + 1,
                BuiltInCustomizations.Plain.DisplayName,
                [.. BuiltInCustomizations.Plain.Bands.Select(band =>
                    new TaxonomyItemInput(band.Key, band.DisplayName, band.Sort))]))).IsSuccess.Should().BeTrue();

            var published = await SendAsync(provider, new PublishTenantLevelTaxonomyCommand(successor));
            published.IsSuccess.Should().BeTrue("a replacement publication commits");
        }

        var rows = (await RowsAsync()).Where(row => row.Operation == "customization.level_taxonomy.publish").ToList();

        rows.Select(row => row.EntityId).Should().BeEquivalentTo(
            [incumbent.ToString(), successor.ToString()]);

        var replacement = rows.Single(row => row.EntityId == successor.ToString());
        replacement.Outcome.Should().Be("success");
        Status(replacement.AfterState).Should().Be("Active");
        Change(replacement, $"/TenantLevelTaxonomy/{incumbent}/Status").Should().Be(("\"Active\"", "\"Deprecated\""));
        replacement.ActorUserId.Should().Be(Actor);
        replacement.CorrelationId.Should().Be(Correlation);

        // No membership claim: the successor was LOADED, not created in this request, and a
        // loaded collection is not known to be complete. Its bands are on the record of the
        // request that created them (ADR-0044 Amendment 6 § 4).
        using var after = JsonDocument.Parse(replacement.AfterState!);
        after.RootElement.TryGetProperty("Items", out _).Should().BeFalse();
    }

    [Fact]
    public async Task The_bands_a_tenant_authors_are_in_the_row_that_records_the_taxonomy()
    {
        // The Major: the interceptor captured every band and the composer kept only the
        // taxonomy's own type, so the persisted row held the parent's metadata and none of
        // the vocabulary the tenant wrote. Asserted on the persisted JSON, because an
        // interceptor-only case never proved the row kept what was captured.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        await SeedAsync(dataSource);

        var taxonomy = Guid.CreateVersion7();

        await using (var provider = Compose(dataSource))
        {
            (await SendAsync(provider, new RegisterTenantLevelTaxonomyCommand(
                taxonomy,
                "belts",
                1,
                new Dictionary<string, string> { ["en"] = "Belts" },
                [
                    new TaxonomyItemInput("white", new Dictionary<string, string> { ["en"] = "Mukyu White" }, 1, """{"color":"#fafafa"}"""),
                    new TaxonomyItemInput("black", new Dictionary<string, string> { ["en"] = "Shodan Black" }, 2),
                ]))).IsSuccess.Should().BeTrue();
        }

        var row = (await RowsAsync()).Single(row =>
            row.Operation == "customization.level_taxonomy.register" && row.EntityId == taxonomy.ToString());

        using var after = JsonDocument.Parse(row.AfterState!);
        var bands = after.RootElement.GetProperty("Items");
        bands.GetProperty("white").GetProperty("DisplayName").GetProperty("en").GetString().Should().Be("Mukyu White");
        bands.GetProperty("white").GetProperty("Metadata").GetProperty("color").GetString().Should().Be("#fafafa");
        bands.GetProperty("black").GetProperty("DisplayName").GetProperty("en").GetString().Should().Be("Shodan Black");

        Change(row, $"/TenantLevelTaxonomy/{taxonomy}/Items/black/DisplayName").After
            .Should().Contain("Shodan Black");

        // And only this aggregate: the seed's own taxonomy — a different instance of the
        // same type, registered in another request — is not in this row.
        row.AfterState.Should().NotContain(BuiltInCustomizations.Plain.Bands[0].DisplayName["en"]);
        row.Changes.Should().NotContain(Tenant.BuiltInTaxonomyId.ToString());
    }

    [Fact]
    public async Task A_removed_band_is_in_the_changes_of_the_persisted_row()
    {
        // No shipped command removes a band yet — remove_band is a (planned) matrix row — so
        // the aggregate method runs on the real context, on the real connection, and the
        // row goes through the real interceptor, capture, composer and store. What this
        // proves is the removal's path to the column, which is the half the planned command
        // will not be able to change.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        await SeedAsync(dataSource);

        var taxonomyId = Guid.CreateVersion7();
        var intentId = AuditEntryId.From(Guid.CreateVersion7());

        await using var provider = Compose(dataSource);

        (await SendAsync(provider, new RegisterTenantLevelTaxonomyCommand(
            taxonomyId,
            "stages",
            1,
            new Dictionary<string, string> { ["en"] = "Stages" },
            [
                new TaxonomyItemInput("early", new Dictionary<string, string> { ["en"] = "Early" }, 1),
                new TaxonomyItemInput("middle", new Dictionary<string, string> { ["en"] = "Middle Stage" }, 2),
                new TaxonomyItemInput("late", new Dictionary<string, string> { ["en"] = "Late" }, 3),
            ]))).IsSuccess.Should().BeTrue();

        await using (var scope = provider.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var unitOfWork = services.GetRequiredService<IUnitOfWork>();
            var capture = services.GetRequiredService<IAuditStateCapture>();

            await using var transaction = await unitOfWork.BeginTransactionAsync();
            await unitOfWork.SetTenantContextAsync(services.GetRequiredService<ITenantContext>());

            // The frame a request would open, declared before the work — as step 3 does.
            capture.OpenFrame();
            capture.DeclareIntent(new AuditIntent(
                intentId,
                Tenant.TenantId,
                OrganizationId: null,
                UserId.From(Actor),
                Correlation,
                "customization",
                "customization.level_taxonomy.remove_band",
                OperationType.Update,
                OperationClass.Must,
                typeof(TenantLevelTaxonomy),
                DateTimeOffset.UtcNow));

            var taxonomies = services.GetRequiredService<ITenantLevelTaxonomyStore>();
            var taxonomy = await taxonomies.FindAsync(TenantLevelTaxonomyId.From(taxonomyId));
            taxonomy!.RemoveItem("middle", services.GetRequiredService<IClock>(), UserId.From(Actor));
            await taxonomies.UpdateAsync(taxonomy);

            await services.GetRequiredService<IAuditStore>().WritePendingAsync(unitOfWork);
            await transaction.CompleteAsync();
            capture.CloseFrame(AuditIntentResult.Succeeded);
        }

        var row = (await RowsAsync()).Single(row => row.Id == intentId.Value);

        var removed = Change(row, $"/TenantLevelTaxonomy/{taxonomyId}/Items/middle/DisplayName");
        removed.Before.Should().Contain("Middle Stage");
        removed.After.Should().Be("null");

        row.Changes.Should().NotContain("/Items/early/").And.NotContain("/Items/late/",
            "an untouched band is not a change");
    }

    [Fact]
    public async Task A_partially_loaded_taxonomy_is_never_recorded_as_owning_only_what_was_loaded()
    {
        // The second review's measurement, kept: a filtered Include brings one band of three
        // and EF still reports the collection loaded. Trusting that, the row recorded a
        // taxonomy with one band — permanently. Membership is now written down only for an
        // owner created in the request, so a loaded owner's collection is left out as
        // unknown, however it was loaded (ADR-0044 Amendment 6 § 4).
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        await SeedAsync(dataSource);

        var intentId = AuditEntryId.From(Guid.CreateVersion7());
        var firstBand = BuiltInCustomizations.Plain.Bands[0].Key;

        await using var provider = Compose(dataSource);
        await using (var scope = provider.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var unitOfWork = services.GetRequiredService<IUnitOfWork>();
            var capture = services.GetRequiredService<IAuditStateCapture>();

            await using var transaction = await unitOfWork.BeginTransactionAsync();
            await unitOfWork.SetTenantContextAsync(services.GetRequiredService<ITenantContext>());

            capture.OpenFrame();
            capture.DeclareIntent(new AuditIntent(
                intentId,
                Tenant.TenantId,
                OrganizationId: null,
                UserId.From(Actor),
                Correlation,
                "customization",
                "customization.level_taxonomy.rename",
                OperationType.Update,
                OperationClass.Must,
                typeof(TenantLevelTaxonomy),
                DateTimeOffset.UtcNow));

            var db = services.GetRequiredService<CustomizationDbContext>();
            var taxonomy = await db.TenantLevelTaxonomies
                .Include(candidate => candidate.Items.Where(band => band.Key == firstBand))
                .SingleAsync(candidate => candidate.Id == TenantLevelTaxonomyId.From(Tenant.BuiltInTaxonomyId));

            taxonomy.Items.Should().ContainSingle("the premise: the load brought one band");
            db.Entry(taxonomy).Collection(nameof(TenantLevelTaxonomy.Items)).IsLoaded.Should().BeTrue(
                "the premise: EF calls a filtered collection loaded");

            taxonomy.Rename(LocalizedText.From(("en", "Renamed levels")),
                services.GetRequiredService<IClock>(), UserId.From(Actor));
            await db.SaveChangesAsync();

            await services.GetRequiredService<IAuditStore>().WritePendingAsync(unitOfWork);
            await transaction.CompleteAsync();
            capture.CloseFrame(AuditIntentResult.Succeeded);
        }

        var row = (await RowsAsync()).Single(row => row.Id == intentId.Value);

        row.AfterState.Should().Contain("Renamed levels");

        using var after = JsonDocument.Parse(row.AfterState!);
        after.RootElement.TryGetProperty("Items", out _).Should().BeFalse(
            "one band of three is not the taxonomy's membership, and the row must not say it is");
    }

    [Fact]
    public async Task A_created_taxonomy_that_loses_bands_to_the_tracker_is_never_recorded_as_owning_the_rest()
    {
        // The third review's measurement, kept: a taxonomy created with three bands and
        // saved, two bands detached, the root renamed and saved again. The database keeps all
        // three, and the row's after_state — the latest capture — listed one. Created in the
        // request is complete only while the tracker holds what the root was created with;
        // past that the membership is unknown, and the first flush's changes still carry
        // every band (ADR-0044 Amendment 6 § 4).
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        await SeedAsync(dataSource);

        var intentId = AuditEntryId.From(Guid.CreateVersion7());
        var taxonomyId = TenantLevelTaxonomyId.From(Guid.CreateVersion7());
        var actor = UserId.From(Actor);

        await using var provider = Compose(dataSource);
        await using (var scope = provider.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var unitOfWork = services.GetRequiredService<IUnitOfWork>();
            var capture = services.GetRequiredService<IAuditStateCapture>();
            var clock = services.GetRequiredService<IClock>();

            await using var transaction = await unitOfWork.BeginTransactionAsync();
            await unitOfWork.SetTenantContextAsync(services.GetRequiredService<ITenantContext>());

            capture.OpenFrame();
            capture.DeclareIntent(new AuditIntent(
                intentId,
                Tenant.TenantId,
                OrganizationId: null,
                actor,
                Correlation,
                "customization",
                "customization.level_taxonomy.register",
                OperationType.Create,
                OperationClass.Must,
                typeof(TenantLevelTaxonomy),
                DateTimeOffset.UtcNow));

            var db = services.GetRequiredService<CustomizationDbContext>();
            var taxonomy = TenantLevelTaxonomy.Create(
                taxonomyId, Tenant.TenantId, "stages", 1, LocalizedText.From(("en", "Stages")), clock, actor);
            taxonomy.AddItem("a", LocalizedText.From(("en", "First")), 1, null, clock, actor);
            taxonomy.AddItem("b", LocalizedText.From(("en", "Second")), 2, null, clock, actor);
            taxonomy.AddItem("c", LocalizedText.From(("en", "Third")), 3, null, clock, actor);
            db.Add(taxonomy);
            await db.SaveChangesAsync();

            foreach (var band in taxonomy.Items.Where(band => band.Key != "a"))
            {
                db.Entry(band).State = EntityState.Detached;
            }

            taxonomy.Rename(LocalizedText.From(("en", "Renamed stages")), clock, actor);
            await db.SaveChangesAsync();

            await services.GetRequiredService<IAuditStore>().WritePendingAsync(unitOfWork);
            await transaction.CompleteAsync();
            capture.CloseFrame(AuditIntentResult.Succeeded);
        }

        (await BandCountAsync("stages")).Should().Be(3, "the premise: detaching removed nothing from the database");

        var row = (await RowsAsync()).Single(row => row.Id == intentId.Value);

        row.AfterState.Should().Contain("Renamed stages");

        using var after = JsonDocument.Parse(row.AfterState!);
        after.RootElement.TryGetProperty("Items", out _).Should().BeFalse(
            "one band of the three the database holds is not the taxonomy's membership");

        Change(row, $"/TenantLevelTaxonomy/{taxonomyId.Value}/Items/c/DisplayName").After
            .Should().Contain("Third", "the first flush recorded every band it wrote");
    }

    [Theory]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(253)]
    public async Task A_host_mapping_of_any_valid_length_commits_with_its_whole_key_on_the_row(int length)
    {
        // The fourth review's measurement, kept: audit_log.entity_id was varchar(100), and a
        // host mapping's key is its host, which platform_host_to_tenant admits to 253
        // characters. From 101 up the MUST row failed with 22001, the mapping rolled back, the
        // standalone record of the attempt failed the same way, and the audit health check
        // went unhealthy. 100 is the control that passed before; the key goes on the row
        // whole, because a truncated key names a different subject.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        await SeedAsync(dataSource);

        var host = HostOfLength(length);

        await using (var provider = Compose(dataSource))
        {
            (await SendAsync(provider, new MapHostToTenantCommand(host))).IsSuccess.Should().BeTrue();

            provider.GetRequiredService<IAuditHealth>().IsHealthy.Should().BeTrue(
                "no audit write failed");
        }

        (await HostMappingCountAsync(host)).Should().Be(1, "the mapping committed");

        var row = (await RowsAsync()).Should().ContainSingle(row =>
            row.Operation == "tenancy.hostmapping.write" && row.EntityId == host).Subject;

        row.Outcome.Should().Be("success");
    }

    [Fact]
    public async Task A_refused_publication_is_recorded_against_its_subject_with_actor_and_correlation()
    {
        // The failure path of the same two columns, written by the other writer. Publishing
        // a revision that is already live is refused after the handler designated it, so
        // the reconcile's standalone row names the instance it refused.
        await using var dataSource = NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString);
        await SeedAsync(dataSource);

        await using (var provider = Compose(dataSource))
        {
            var refused = await SendAsync(provider, new PublishTenantContentTypeCommand(Tenant.BuiltInContentTypeId));
            refused.IsFailure.Should().BeTrue("the precondition: that revision is already live");
        }

        var row = (await RowsAsync()).Single(row =>
            row.Operation == "customization.content_type.publish" && row.Outcome != "success");

        // error_key is the refusal's MESSAGE key; the specific reason — not a draft — is a
        // field detail of that error and not a column.
        row.Outcome.Should().Be("failed");
        row.ErrorKey.Should().Be("lockey_business_rule_violation");
        row.EntityId.Should().Be(Tenant.BuiltInContentTypeId.ToString());
        row.ActorUserId.Should().Be(Actor);
        row.CorrelationId.Should().Be(Correlation);
    }

    private static async Task<SharedKernel.Results.IResultBase> SendAsync<TResponse>(
        ServiceProvider provider, IRequest<TResponse> command)
        where TResponse : SharedKernel.Results.IResultBase
    {
        await using var scope = provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(command);
    }

    private static string? Status(string? state) =>
        state is null ? null : JsonDocument.Parse(state).RootElement.GetProperty("Status").GetString();

    private static (string Before, string After) Change(Row row, string path)
    {
        using var changes = JsonDocument.Parse(row.Changes!);

        var entry = changes.RootElement.EnumerateArray()
            .Single(change => change.GetProperty("path").GetString() == path);

        return (entry.GetProperty("before").GetRawText(), entry.GetProperty("after").GetRawText());
    }

    private static async Task SeedAsync(NpgsqlDataSource dataSource)
    {
        var runner = new SeedRunner(
            context => SeedComposition.Build(dataSource, context, NullLoggerFactory.Instance),
            NullLogger<SeedRunner>.Instance);

        (await runner.RunAsync(CancellationToken.None, [Tenant])).Should().Be(0);
    }

    /// <summary>The seeder's graph, acting for the tenant as an authenticated principal would.</summary>
    private static ServiceProvider Compose(NpgsqlDataSource dataSource) =>
        SeedComposition.Build(
            dataSource,
            new ActingContext(Tenant.TenantId, Tenant.DefaultOrganization.OrganizationId),
            NullLoggerFactory.Instance);

    /// <summary>
    /// <see cref="SeedTenantContext"/> with a principal and a trace — the two things a seed
    /// never carries, and therefore the two a seed-driven case could never check.
    /// </summary>
    private sealed class ActingContext(TenantId tenantId, OrganizationId organizationId) : ITenantContext
    {
        public bool IsResolved => true;

        public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;

        public TenantId TenantId => tenantId;

        public OrganizationId? OrganizationId => organizationId;

        public UserId? UserId => LearnStack.SharedKernel.Identifiers.UserId.From(Actor);

        public string? CorrelationId => Correlation;

        public string? ModuleName => null;
    }

    private sealed record Row(
        Guid Id,
        string Operation,
        string Outcome,
        string? EntityId,
        string? BeforeState,
        string? AfterState,
        string? Changes,
        Guid? ActorUserId,
        string? CorrelationId,
        string? ErrorKey);

    /// <summary>Reads the tenant's rows as <c>learnstack_platform</c> — the read is the only bypass.</summary>
    private async Task<IReadOnlyList<Row>> RowsAsync()
    {
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);
        await using var command = new NpgsqlCommand(
            """
            SELECT id, operation, outcome, entity_id, before_state::text, after_state::text,
                   changes::text, actor_user_id, correlation_id, error_key
            FROM audit_log
            WHERE tenant_id = @tenant
            ORDER BY timestamp
            """, (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("tenant", Tenant.TenantId.Value);

        var rows = new List<Row>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows.Add(new Row(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetGuid(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return rows;
    }

    /// <summary>A valid DNS host of exactly <paramref name="length"/> characters, no label over 63.</summary>
    private static string HostOfLength(int length)
    {
        const string Suffix = ".example";
        var remaining = length - Suffix.Length;
        var labels = new List<string>();

        while (remaining > 0)
        {
            var take = Math.Min(63, remaining);

            // Never leave one character for a label after the dot that follows this one.
            if (remaining - take == 1)
            {
                take--;
            }

            labels.Add(new string('h', take));
            remaining -= take;

            if (remaining > 0)
            {
                remaining--;
            }
        }

        var host = string.Join('.', labels) + Suffix;
        host.Length.Should().Be(length, "the premise: the host is exactly the length the case names");

        return host;
    }

    /// <summary>How many mappings the database holds for a host, read as the platform role.</summary>
    private async Task<long> HostMappingCountAsync(string host)
    {
        await using var connection = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM platform_host_to_tenant WHERE host = @host", (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("host", host);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>The bands the database holds under a taxonomy key, read as the owner under the tenant.</summary>
    private async Task<long> BandCountAsync(string taxonomyKey)
    {
        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(owner, transaction, Tenant.TenantId.Value);

        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM tenant_level_taxonomy_items WHERE taxonomy_key = @key",
            (NpgsqlConnection)owner,
            (NpgsqlTransaction)transaction);
        command.Parameters.AddWithValue("key", taxonomyKey);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>The same cleanup as <see cref="AuditPipelineTests"/>, for the same reasons.</summary>
    private async Task CleanUpAsync()
    {
        var ids = SeedData.All.Select(tenant => tenant.TenantId.Value).ToArray();

        await using (var platform = await PostgresFixture.OpenAsync(_schema.Postgres.PlatformConnectionString))
        {
            foreach (var statement in new[]
            {
                "DELETE FROM audit_log WHERE tenant_id = ANY(@ids)",
                "DELETE FROM platform_host_to_tenant WHERE tenant_id = ANY(@ids)",
                "UPDATE tenants SET default_organization_id = NULL WHERE id = ANY(@ids)",
                "DELETE FROM organizations WHERE tenant_id = ANY(@ids)",
                "DELETE FROM tenants WHERE id = ANY(@ids)",
            })
            {
                await using var cleanup = new NpgsqlCommand(statement, (NpgsqlConnection)platform);
                cleanup.Parameters.AddWithValue("ids", ids);
                await cleanup.ExecuteNonQueryAsync();
            }
        }

        await using var owner = await PostgresFixture.OpenAsync(_schema.Postgres.MigrationConnectionString);

        foreach (var tenant in SeedData.All)
        {
            await using var transaction = await owner.BeginTransactionAsync();
            await SchemaQueries.SetTenantAsync(owner, transaction, tenant.TenantId.Value);

            foreach (var statement in new[]
            {
                "DELETE FROM tenant_level_taxonomy_items WHERE tenant_id = @tenant",
                "DELETE FROM tenant_level_taxonomies WHERE tenant_id = @tenant",
                "DELETE FROM tenant_content_types WHERE tenant_id = @tenant",
                "DELETE FROM customization_generations WHERE tenant_id = @tenant",
                "DELETE FROM tenant_domains WHERE tenant_id = @tenant",
                "DELETE FROM tenant_locales WHERE tenant_id = @tenant",
            })
            {
                await SchemaQueries.ExecuteAsync(owner, transaction, statement, ("tenant", tenant.TenantId.Value));
            }

            await transaction.CommitAsync();
        }
    }
}
