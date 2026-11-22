using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Application.DataProtection;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Unit.Application.DataProtection;

/// <summary>ADR-0107 § 3–§ 4: one JSON document, bounded sections, the ceiling, truncation and chunking.</summary>
public sealed class DataExportBundleBuilderTests
{
    private static readonly DateTimeOffset GeneratedAt = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_bundle_is_one_document_with_its_envelope_and_a_section_per_participant_in_registry_order()
    {
        var subject = Subject(organization: true);
        var builder = Builder(DataExportOptions.Default,
            new Participant("zeta", writer => Append(writer, "items", "{\"value\":2}")),
            new Participant("alpha", async writer =>
            {
                await Append(writer, "first", "{\"value\":1}");
                await Append(writer, "first", "[1,2]");
                await Append(writer, "second", "\"text\"");
            }),
            new Participant("empty", _ => Task.CompletedTask));

        var result = await builder.BuildAsync(subject, GeneratedAt, CancellationToken.None);

        using var bundle = result.Bundle!;
        using var document = await Read(bundle);
        var root = document.RootElement;
        root.EnumerateObject().Select(property => property.Name).Should()
            .Equal("schema_version", "request", "generated_at", "sections", "truncated");
        root.GetProperty("schema_version").GetInt32().Should().Be(DataExportBundleBuilder.SchemaVersion);
        root.GetProperty("request").GetProperty("id").GetString().Should().Be(subject.RequestId.ToString("D"));
        root.GetProperty("request").GetProperty("kind").GetString().Should().Be("export");
        root.GetProperty("request").GetProperty("origin").GetString().Should().Be("self");
        root.GetProperty("request").GetProperty("scope").GetString().Should().Be("organization");
        root.GetProperty("generated_at").GetString().Should().Be("2026-11-22T10:00:00.0000000Z");
        var sections = root.GetProperty("sections");
        sections.EnumerateObject().Select(section => section.Name).Should().Equal("alpha", "empty", "zeta");
        sections.GetProperty("alpha").GetProperty("first").GetArrayLength().Should().Be(2);
        sections.GetProperty("alpha").GetProperty("second")[0].GetString().Should().Be("text");
        sections.GetProperty("empty").EnumerateObject().Should().BeEmpty();
        sections.GetProperty("zeta").GetProperty("items")[0].GetProperty("value").GetInt32().Should().Be(2);
        root.GetProperty("truncated").EnumerateObject().Should().BeEmpty();
        bundle.Truncations.Should().BeEmpty();
        bundle.ChunkCount.Should().Be(1);
    }

    [Fact]
    public async Task A_section_cap_refuses_the_record_that_would_pass_it_and_the_declared_truncation_is_stated()
    {
        var outcomes = new List<UserDataExportAppend>();
        var record = "{\"padding\":\"" + new string('x', 900) + "\"}";
        var builder = Builder(DataExportOptions.Default, new Participant("audit", async writer =>
        {
            for (var index = 0; index < 50; index++)
            {
                var outcome = await Append(writer, "entries", record);
                outcomes.Add(outcome);
                if (outcome == UserDataExportAppend.SectionCapReached)
                {
                    writer.DeclareTruncated("entries", index, 50);
                    return;
                }
            }
        }, cap: 10_000));

        var result = await builder.BuildAsync(Subject(), GeneratedAt, CancellationToken.None);

        using var bundle = result.Bundle!;
        outcomes.Should().EndWith(UserDataExportAppend.SectionCapReached);
        var included = outcomes.Count(outcome => outcome == UserDataExportAppend.Appended);
        included.Should().BeInRange(9, 10);
        using var document = await Read(bundle);
        document.RootElement.GetProperty("sections").GetProperty("audit").GetProperty("entries").GetArrayLength().Should().Be(included);
        var truncated = document.RootElement.GetProperty("truncated").GetProperty("audit.entries");
        truncated.GetProperty("included").GetInt64().Should().Be(included);
        truncated.GetProperty("total").GetInt64().Should().Be(50);
        bundle.Truncations.Should().ContainSingle().Which.Should().Be(new DataExportTruncation("audit.entries", included, 50));
    }

    [Fact]
    public async Task At_the_smallest_ceiling_the_audit_cap_is_clamped_to_half_of_it_so_the_section_truncates_instead_of_failing()
    {
        var options = new DataExportOptions(DataRequestLimits.MinimumBundleBytes);
        var record = "{\"padding\":\"" + new string('a', 1_000) + "\"}";
        long included = 0;
        var builder = Builder(options, new Participant("audit", async writer =>
        {
            while (true)
            {
                var outcome = await Append(writer, "entries", record);
                if (outcome != UserDataExportAppend.Appended)
                {
                    outcome.Should().Be(UserDataExportAppend.SectionCapReached, "the clamped cap is reached before the ceiling");
                    writer.DeclareTruncated("entries", included, 100_000);
                    return;
                }

                included++;
            }
        }, cap: DataRequestLimits.AuditSectionBytes), new Participant("later", writer => Append(writer, "items", "1")));

        var result = await builder.BuildAsync(Subject(), GeneratedAt, CancellationToken.None);

        result.IsTooLarge.Should().BeFalse("an unclamped 8 MiB cap would never apply under a 1 MiB ceiling");
        using var bundle = result.Bundle!;
        bundle.ByteCount.Should().BeLessThanOrEqualTo(options.MaxBytes);
        included.Should().BeGreaterThan(0);
        (included * (record.Length + 1L)).Should().BeLessThanOrEqualTo(options.MaxBytes / 2);
        bundle.Truncations.Should().ContainSingle().Which.Should().Be(new DataExportTruncation("audit.entries", included, 100_000));
        using var document = await Read(bundle);
        document.RootElement.GetProperty("sections").GetProperty("later").GetProperty("items").GetArrayLength().Should().Be(1,
            "the sections after a truncated one are still written");
    }

    [Fact]
    public async Task A_collection_cap_truncates_only_its_collection_and_is_clamped_to_half_the_ceiling()
    {
        var options = new DataExportOptions(DataRequestLimits.MinimumBundleBytes);
        var record = "{\"padding\":\"" + new string('i', 500) + "\"}";
        var outcomes = new List<UserDataExportAppend>();
        long issued = 0;
        var builder = Builder(options, new Participant("identity", async writer =>
        {
            for (var index = 0; index < 100; index++)
            {
                (await Append(writer, "accepted", record)).Should().Be(UserDataExportAppend.Appended);
            }

            while (true)
            {
                var outcome = await Append(writer, "issued", record);
                outcomes.Add(outcome);
                if (outcome != UserDataExportAppend.Appended)
                {
                    writer.DeclareTruncated("issued", issued, issued + 1);
                    return;
                }

                issued++;
            }
        }, collectionCaps: new Dictionary<string, long> { ["issued"] = DataRequestLimits.IssuedInvitationsCollectionBytes }));

        var result = await builder.BuildAsync(Subject(), GeneratedAt, CancellationToken.None);

        result.IsTooLarge.Should().BeFalse();
        using var bundle = result.Bundle!;
        outcomes[^1].Should().Be(UserDataExportAppend.SectionCapReached);
        (issued * (record.Length + 1L)).Should().BeLessThanOrEqualTo(options.MaxBytes / 2,
            "the 4 MiB collection cap is clamped to half of the 1 MiB ceiling");
        using var document = await Read(bundle);
        var section = document.RootElement.GetProperty("sections").GetProperty("identity");
        section.GetProperty("accepted").GetArrayLength().Should().Be(100, "an uncapped collection of the same section is whole");
        section.GetProperty("issued").GetArrayLength().Should().Be((int)issued);
        bundle.Truncations.Should().ContainSingle().Which.Collection.Should().Be("identity.issued");
    }

    [Fact]
    public async Task A_collection_cap_below_the_ceiling_applies_unclamped()
    {
        var builder = Builder(DataExportOptions.Default, new Participant("identity", async writer =>
        {
            (await Append(writer, "issued", "\"" + new string('s', 4_000) + "\"")).Should().Be(UserDataExportAppend.Appended);
            (await Append(writer, "issued", "\"" + new string('s', 4_000) + "\"")).Should().Be(UserDataExportAppend.SectionCapReached);
            (await Append(writer, "other", "\"" + new string('s', 4_000) + "\"")).Should().Be(UserDataExportAppend.Appended);
        }, collectionCaps: new Dictionary<string, long> { ["issued"] = 6_000 }));

        using var bundle = (await builder.BuildAsync(Subject(), GeneratedAt, CancellationToken.None)).Bundle!;

        bundle.ByteCount.Should().BeGreaterThan(8_000);
    }

    [Theory]
    [InlineData(DataRequestLimits.AuditSectionBytes, DataRequestLimits.DefaultMaximumBundleBytes, DataRequestLimits.AuditSectionBytes)]
    [InlineData(DataRequestLimits.AuditSectionBytes, DataRequestLimits.MinimumBundleBytes, DataRequestLimits.MinimumBundleBytes / 2)]
    [InlineData(DataRequestLimits.IssuedInvitationsCollectionBytes, DataRequestLimits.DefaultMaximumBundleBytes,
        DataRequestLimits.IssuedInvitationsCollectionBytes)]
    [InlineData(4_096L, DataRequestLimits.MaximumBundleBytes, 4_096L)]
    public void A_declared_cap_is_clamped_to_half_the_ceiling(long declared, long ceiling, long expected) =>
        DataExportBundleBuilder.EffectiveCap(declared, ceiling).Should().Be(expected);

    [Theory]
    [InlineData("section")]
    [InlineData("collection_value")]
    [InlineData("collection_name")]
    public async Task A_cap_outside_the_bundle_range_or_on_a_malformed_collection_is_a_programming_error(string misuse)
    {
        var builder = Builder(DataExportOptions.Default, new Participant("capped", _ => Task.CompletedTask,
            cap: misuse == "section" ? 0 : null,
            collectionCaps: misuse switch
            {
                "collection_value" => new Dictionary<string, long> { ["items"] = DataRequestLimits.MaximumBundleBytes + 1 },
                "collection_name" => new Dictionary<string, long> { ["Bad-Name"] = 1_024 },
                _ => null,
            }));

        await FluentActions.Invoking(() => builder.BuildAsync(Subject(), GeneratedAt, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task The_bundle_ceiling_fails_the_whole_bundle_and_refuses_every_later_record()
    {
        var outcomes = new List<UserDataExportAppend>();
        var record = "\"" + new string('y', 100_000) + "\"";
        var builder = Builder(new DataExportOptions(DataRequestLimits.MinimumBundleBytes),
            new Participant("first", async writer =>
            {
                for (var index = 0; index < 20; index++)
                {
                    outcomes.Add(await Append(writer, "items", record));
                }
            }),
            new Participant("second", _ => throw new InvalidOperationException("A refused bundle runs no later participant.")));

        var result = await builder.BuildAsync(Subject(), GeneratedAt, CancellationToken.None);

        result.IsTooLarge.Should().BeTrue();
        result.Bundle.Should().BeNull();
        outcomes.Should().Contain(UserDataExportAppend.BundleCeilingReached);
        outcomes.SkipWhile(outcome => outcome == UserDataExportAppend.Appended)
            .Should().OnlyContain(outcome => outcome == UserDataExportAppend.BundleCeilingReached);
    }

    [Fact]
    public async Task A_finished_bundle_never_passes_the_ceiling_however_close_the_records_come()
    {
        var options = new DataExportOptions(DataRequestLimits.MinimumBundleBytes);
        var builder = Builder(options, new Participant("fill", async writer =>
        {
            while (await Append(writer, "items", "\"" + new string('z', 997) + "\"") == UserDataExportAppend.Appended)
            {
            }
        }, cap: 900_000), new Participant("tail", async writer =>
        {
            while (await Append(writer, "items", "1") == UserDataExportAppend.Appended)
            {
            }
        }));

        var result = await builder.BuildAsync(Subject(), GeneratedAt, CancellationToken.None);

        result.IsTooLarge.Should().BeTrue("the tail fills the bundle to its ceiling");
        var bounded = Builder(options, new Participant("fill", async writer =>
        {
            while (await Append(writer, "items", "\"" + new string('z', 997) + "\"") == UserDataExportAppend.Appended)
            {
            }
        }, cap: 900_000));
        using var bundle = (await bounded.BuildAsync(Subject(), GeneratedAt, CancellationToken.None)).Bundle!;
        bundle.ByteCount.Should().BeLessThanOrEqualTo(options.MaxBytes);
        using var _ = await Read(bundle);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{} {}")]
    [InlineData("1 2")]
    [InlineData("/* comment */ 1")]
    [InlineData("{\"a\":1,}")]
    public async Task A_record_that_is_not_exactly_one_JSON_value_is_refused_before_any_byte_is_written(string record)
    {
        var builder = Builder(DataExportOptions.Default, new Participant("bad", writer => Append(writer, "items", record)));

        var act = () => builder.BuildAsync(Subject(), GeneratedAt, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("return")]
    [InlineData("name")]
    [InlineData("late")]
    [InlineData("twice")]
    public async Task The_section_writer_is_append_only_and_bounded_to_its_participant(string misuse)
    {
        IUserDataExportWriter? kept = null;
        var builder = Builder(DataExportOptions.Default, new Participant("misuse", async writer =>
        {
            kept = writer;
            switch (misuse)
            {
                case "return":
                    await Append(writer, "a", "1");
                    await Append(writer, "b", "2");
                    await Append(writer, "a", "3");
                    break;
                case "name":
                    await Append(writer, "Bad-Name", "1");
                    break;
                case "twice":
                    writer.DeclareTruncated("a", 1, 2);
                    writer.DeclareTruncated("a", 1, 2);
                    break;
            }
        }));

        var act = () => builder.BuildAsync(Subject(), GeneratedAt, CancellationToken.None);

        if (misuse == "late")
        {
            using var bundle = (await act()).Bundle!;
            await FluentActions.Invoking(() => kept!.AppendAsync("a", Encoding.UTF8.GetBytes("1"), CancellationToken.None).AsTask())
                .Should().ThrowAsync<InvalidOperationException>();
            FluentActions.Invoking(() => kept!.DeclareTruncated("a", 0, 1)).Should().Throw<InvalidOperationException>();
            kept!.RemainingBytes.Should().Be(0);
        }
        else if (misuse == "name")
        {
            await act.Should().ThrowAsync<ArgumentException>();
        }
        else
        {
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
    }

    [Fact]
    public async Task A_large_bundle_is_chunked_into_full_chunks_and_one_remainder_that_concatenate_to_the_document()
    {
        var record = "\"" + new string('c', 50_000) + "\"";
        var builder = Builder(DataExportOptions.Default, new Participant("big", async writer =>
        {
            for (var index = 0; index < 10; index++)
            {
                (await Append(writer, "items", record)).Should().Be(UserDataExportAppend.Appended);
            }
        }));

        using var bundle = (await builder.BuildAsync(Subject(), GeneratedAt, CancellationToken.None)).Bundle!;

        var chunks = new List<byte[]>();
        await foreach (var chunk in bundle.ReadChunksAsync())
        {
            chunks.Add(chunk.ToArray());
        }

        bundle.ChunkCount.Should().Be(DataRequestLimits.ChunkCountFor(bundle.ByteCount)).And.Be(chunks.Count).And.BeGreaterThan(1);
        chunks.SkipLast(1).Should().OnlyContain(chunk => chunk.Length == DataRequestLimits.ChunkPlaintextBytes);
        chunks[^1].Length.Should().BeInRange(1, DataRequestLimits.ChunkPlaintextBytes);
        chunks.Sum(chunk => (long)chunk.Length).Should().Be(bundle.ByteCount);
        using var document = JsonDocument.Parse(chunks.SelectMany(chunk => chunk).ToArray());
        document.RootElement.GetProperty("sections").GetProperty("big").GetProperty("items").GetArrayLength().Should().Be(10);
    }

    [Fact]
    public async Task Only_an_export_with_a_utc_generation_time_builds_a_bundle()
    {
        var builder = Builder(DataExportOptions.Default);
        var erasure = new UserDataSubject(Guid.CreateVersion7(), DataRequestKind.Erasure, DataRequestOrigin.Self,
            UserId.From(Guid.CreateVersion7()), new DataRequestScopeGroup(TenantId.From(Guid.CreateVersion7()), null),
            [Guid.CreateVersion7()]);

        await FluentActions.Invoking(() => builder.BuildAsync(erasure, GeneratedAt, CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Invoking(() => builder.BuildAsync(Subject(), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(3)),
            CancellationToken.None)).Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(null, DataRequestLimits.DefaultMaximumBundleBytes)]
    [InlineData("1048576", 1_048_576L)]
    [InlineData("67108864", DataRequestLimits.MaximumBundleBytes)]
    public void The_ceiling_setting_defaults_to_sixteen_mebibytes_and_admits_its_range(string? setting, long expected) =>
        DataExportOptions.FromSetting(setting).MaxBytes.Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1048575")]
    [InlineData("67108865")]
    [InlineData("-1")]
    [InlineData(" 1048576")]
    [InlineData("1e7")]
    public void An_invalid_ceiling_setting_fails_fast(string setting) =>
        FluentActions.Invoking(() => DataExportOptions.FromSetting(setting)).Should().Throw<InvalidOperationException>();

    private static DataExportBundleBuilder Builder(DataExportOptions options, params IUserDataParticipant[] participants) =>
        new(new UserDataParticipantRegistry(participants), options);

    private static UserDataSubject Subject(bool organization = false) => new(Guid.CreateVersion7(), DataRequestKind.Export,
        DataRequestOrigin.Self, UserId.From(Guid.CreateVersion7()),
        new DataRequestScopeGroup(TenantId.From(Guid.CreateVersion7()), organization ? OrganizationId.From(Guid.CreateVersion7()) : null),
        [Guid.CreateVersion7()]);

    private static Task<UserDataExportAppend> Append(IUserDataExportWriter writer, string collection, string record) =>
        writer.AppendAsync(collection, Encoding.UTF8.GetBytes(record), CancellationToken.None).AsTask();

    private static async Task<JsonDocument> Read(IDataExportBundle bundle)
    {
        var bytes = new List<byte>();
        await foreach (var chunk in bundle.ReadChunksAsync())
        {
            bytes.AddRange(chunk.ToArray());
        }

        bytes.Should().HaveCount((int)bundle.ByteCount);
        return JsonDocument.Parse(bytes.ToArray());
    }

    private sealed class Participant(string key, Func<IUserDataExportWriter, Task> export, long? cap = null,
        IReadOnlyDictionary<string, long>? collectionCaps = null) : IUserDataParticipant
    {
        public string Key => key;

        public long? ExportSectionCapBytes => cap;

        public IReadOnlyDictionary<string, long> ExportCollectionCapBytes => collectionCaps ?? new Dictionary<string, long>();

        public Task ExportAsync(UserDataSubject subject, IUserDataExportWriter writer, CancellationToken cancellationToken) => export(writer);

        public Task<IReadOnlyCollection<RetainedReferenceCount>> DescribeRetainedAsync(UserDataSubject subject,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyCollection<RetainedReferenceCount>>([]);
    }
}
