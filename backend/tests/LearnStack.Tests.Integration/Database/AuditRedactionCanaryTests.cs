using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Secrets;
using LearnStack.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0108 §§ 4–7 against PostgreSQL: every path every registered <see cref="IUserReferenceLocator"/> declares is
/// redacted for the subject and left alone for anyone else, as <c>learnstack_platform</c> on a work transaction that
/// every case rolls back, so nothing the shared sweeps count moves.
/// </summary>
/// <remarks>
/// <para>
/// <b>A canary per declared path, generated from the declaration.</b> Each declaration gets a row in the capture's
/// exact shape (<c>CapturedEntityChange</c>: the entity's property, a contained collection's member keyed by its own
/// key with a <c>/</c> escaped as <c>~1</c> in the <c>changes</c> path, an array of identifiers) holding the subject's
/// identifier of the declaration's kind beside another person's at the same path, a twin holding only another
/// person's, and a twin holding the subject's identifier of the other kind. A declaration a module adds is a canary
/// here without a line of this file, and a shape the generator cannot build fails it, so no declared path goes
/// unproven. <see cref="Real_captures_are_redacted_at_every_path_shape"/> drives the real capture for each shape.
/// </para>
/// <para>
/// The rows' tenants are this class's own and not in <c>tenants</c>, which <c>audit_log</c> does not require.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed partial class AuditRedactionCanaryTests(SchemaFixture schema)
{
    private static readonly Lazy<IReadOnlyList<IUserReferenceLocator>> Registered = new(RegisteredLocators);

    private static readonly string[] ModulesWithLocators = ["Customization", "Education", "Identity", "Tenancy"];

    private static readonly Guid Subject = Guid.Parse("0190c0de-5b1e-7aaa-8000-00000000c0de");
    private static readonly Guid Membership1 = Guid.Parse("0190c0de-5b1e-7aaa-8000-0000000000a1");
    private static readonly Guid Membership2 = Guid.Parse("0190c0de-5b1e-7aaa-8000-0000000000a2");
    private static readonly Guid OtherPerson = Guid.Parse("0190adad-0000-7aaa-8000-00000000adad");
    private static readonly Guid ThirdPerson = Guid.Parse("0190bbbb-0000-7aaa-8000-00000000bbbb");
    private static readonly Guid OtherMembership = Guid.Parse("0190adad-0000-7aaa-8000-0000000000a9");

    /// <summary>Every module that registers a locator through the API composition.</summary>
    public static TheoryData<string> LocatorModules()
    {
        var data = new TheoryData<string>();
        foreach (var module in Registered.Value.Select(ModuleOf).Order(StringComparer.Ordinal))
        {
            data.Add(module);
        }

        return data;
    }

    [Fact]
    public void Every_module_with_a_locator_has_a_canary_and_every_declaration_a_case()
    {
        // The census over canary coverage: the theory below runs once per module that registers a locator, and each
        // run generates one case per declaration, so a module or a declared path cannot go without a canary.
        LocatorModules().Cast<object[]>().Select(row => (string)row[0]).Should().BeEquivalentTo(ModulesWithLocators);
        Registered.Value.Sum(locator => locator.Redactable.Count)
            .Should().Be(new UserReferenceCatalog(Registered.Value).Redactable.Length);
        Registered.Value.SelectMany(locator => locator.Redactable).Select(declaration => declaration.Path.Shape).Distinct()
            .Should().BeEquivalentTo(Enum.GetValues<UserReferenceShape>(),
                "the shipped declarations exercise every shape of the grammar");
    }

    [Theory]
    [MemberData(nameof(LocatorModules))]
    public async Task Every_declared_path_of_a_module_is_redacted_for_the_subject_only(string module)
    {
        var tenant = Guid.CreateVersion7();
        var declarations = Registered.Value.Single(locator => ModuleOf(locator) == module).Redactable;
        declarations.Should().NotBeEmpty("a locator that lost every declaration would otherwise pass this module vacuously");
        var rows = declarations.SelectMany(declaration => Canary(declaration, tenant)).ToList();
        rows.Should().HaveCount(3 * declarations.Count);

        await using var scope = await OpenAsync(schema.Postgres.PlatformConnectionString);
        await InsertAsync(scope, rows);
        var before = await ReadAsync(scope, rows);

        var report = await RedactAsync(scope, Plan(tenant, DataRequestOrigin.Self, organization: null));

        var after = await ReadAsync(scope, rows);
        var own = rows.Where(row => row.Selected).ToList();
        own.Should().HaveCount(declarations.Count);
        foreach (var row in rows)
        {
            var was = before[row.Id];
            var now = after[row.Id];
            now.Retained.Should().Be(was.Retained, "actor, entity, metadata and every column outside the grant survive ({0})", row.Label);
            if (!row.Selected)
            {
                now.Should().Be(was, "a row that names the subject at no declared path of its kind is not touched ({0})", row.Label);
                continue;
            }

            (now.IpAddress, now.UserAgent, now.ActorEmail).Should().Be((null, null, null), row.Label);
            JsonNode.DeepEquals(Parse(now.BeforeText), row.ExpectedBefore).Should().BeTrue("{0}: {1}", row.Label, now.BeforeText);
            JsonNode.DeepEquals(Parse(now.AfterText), row.ExpectedAfter).Should().BeTrue("{0}: {1}", row.Label, now.AfterText);
            JsonNode.DeepEquals(Parse(now.ChangesText), row.ExpectedChanges).Should().BeTrue("{0}: {1}", row.Label, now.ChangesText);
        }

        report.RowsSelected.Should().Be(own.Count);
        report.Redacted.Values.Should().AllSatisfy(count => count.Should().Be(own.Count),
            "every canary row the subject is named in carries all six columns");
        Retained(report).Should().Equal(new Dictionary<RetainedReferenceKind, long>
        {
            [RetainedReferenceKind.AuditActorReference] = 0,
            [RetainedReferenceKind.AuditEntityReference] = 0,
            [RetainedReferenceKind.AuditMetadataReference] = 0,
        });

        // Idempotent: the re-run rewrites nothing. It selects nothing either, because a payload that no longer holds the
        // subject's identifier no longer names the subject; a row the actor or the entity selects stays selected.
        var again = await RedactAsync(scope, Plan(tenant, DataRequestOrigin.Self, organization: null));
        again.Redacted.Values.Should().AllSatisfy(count => count.Should().Be(0));
        again.RowsSelected.Should().Be(0);
        (await ReadAsync(scope, rows)).Should().Equal(after);
        await scope.Transaction.RollbackAsync();
    }

    [Fact]
    public async Task The_reach_follows_who_asked_another_tenant_is_never_touched_and_the_report_counts_what_survives()
    {
        var tenant = Guid.CreateVersion7();
        var foreign = Guid.CreateVersion7();
        var branch = Guid.CreateVersion7();
        var otherBranch = Guid.CreateVersion7();
        var rows = Reach(tenant, foreign, branch, otherBranch);

        // A person's own request reaches the whole tenant; an administrator's only the group's organization, or only the
        // tenant level for the tenant-wide group, and reports what lies outside as unscoped (ADR-0108 § 4).
        var cases = new (DataRequestOrigin Origin, Guid? Organization, string[] Selected, long Actor, long Entity, long Metadata, long? Unscoped)[]
        {
            (DataRequestOrigin.Self, branch,
                ["actor/branch", "actor/other-branch", "actor/tenant", "actor+authorization/branch", "elided/branch",
                 "entity/branch", "entity/tenant", "payload/branch", "payload/other-branch", "payload/tenant"], 5, 2, 2, null),
            (DataRequestOrigin.Administrator, branch,
                ["actor/branch", "actor+authorization/branch", "elided/branch", "entity/branch", "payload/branch"], 3, 1, 2, 3),
            (DataRequestOrigin.Administrator, null,
                ["actor/tenant", "entity/tenant", "payload/tenant"], 1, 1, 0, 5),
        };
        foreach (var (origin, organization, selected, actor, entity, metadata, unscoped) in cases)
        {
            await using var scope = await OpenAsync(schema.Postgres.PlatformConnectionString);
            await InsertAsync(scope, rows);
            var before = await ReadAsync(scope, rows);

            var report = await RedactAsync(scope, Plan(tenant, origin, organization));

            var after = await ReadAsync(scope, rows);
            var changed = rows.Where(row => after[row.Id] != before[row.Id]).Select(row => row.Label).ToList();
            changed.Should().BeEquivalentTo(selected, "{0} in {1}", origin, organization is null ? "the tenant-wide group" : "an organization group");
            rows.Where(row => row.Tenant == foreign).Should().OnlyContain(row => after[row.Id] == before[row.Id],
                "another tenant's rows naming the same person are outside every reach");
            if (selected.Contains("elided/branch"))
            {
                after[rows.Single(row => row.Label == "elided/branch").Id].BeforeText.Should()
                    .Contain("_elided", "an elided state holds no value, so it has no path to redact");
            }

            report.RowsSelected.Should().Be(selected.Length);
            var expected = new Dictionary<RetainedReferenceKind, long>
            {
                [RetainedReferenceKind.AuditActorReference] = actor,
                [RetainedReferenceKind.AuditEntityReference] = entity,
                [RetainedReferenceKind.AuditMetadataReference] = metadata,
            };
            if (unscoped is { } outside)
            {
                expected[RetainedReferenceKind.UnscopedAuditRows] = outside;
            }

            Retained(report).Should().Equal(expected, "{0} in {1}", origin, organization);
            report.Redacted[AuditRedactedColumn.IpAddress].Should().Be(selected.Length, "every selected row carried an address");

            // The re-run changes nothing and keeps the rows the actor or the entity selects.
            var again = await RedactAsync(scope, Plan(tenant, origin, organization));
            again.Redacted.Values.Should().AllSatisfy(count => count.Should().Be(0));
            again.RowsSelected.Should().Be(selected.Count(label => !label.StartsWith("payload/", StringComparison.Ordinal)));
            Retained(again).Should().Equal(expected);
            (await ReadAsync(scope, rows)).Should().Equal(after);
            await scope.Transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task A_refused_admission_or_plan_runs_no_statement()
    {
        var tenant = Guid.CreateVersion7();
        var rows = Reach(tenant, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await using var scope = await OpenAsync(schema.Postgres.PlatformConnectionString);
        await InsertAsync(scope, rows);
        var before = await ReadAsync(scope, rows);
        var jit = await SettingAsync(scope, "jit");
        var executor = Executor();
        var plan = Plan(tenant, DataRequestOrigin.Self, organization: null);

        // Every other registered operation, the right operation over a closure's plan, and an admission no scope spent.
        const string Sanction = "*does not name an operation that may redact*";
        var refusals = PlatformOperations.All.Where(operation => !ReferenceEquals(operation, PlatformOperations.ErasureComplete))
            .Select(operation => (Name: operation.AuditSlug, Plan: plan, Admission: Spent(operation), Message: Sanction))
            .Append(("closure plan", ClosurePlan(tenant), Spent(PlatformOperations.ErasureComplete), Sanction))
            .Append(("unspent admission", plan, TestPlatformAdmissions.Admission(PlatformOperations.ErasureComplete),
                "*platform scope its admission opened*"))
            .ToList();
        refusals.Should().HaveCount(PlatformOperations.All.Count + 1);
        foreach (var (name, refusedPlan, admission, message) in refusals)
        {
            await FluentActions.Awaiting(() => executor.RedactAsync(refusedPlan, admission, scope.Connection, scope.Transaction))
                .Should().ThrowAsync<InvalidOperationException>(name).WithMessage(message);
        }

        // Nothing ran: the first statement switches JIT off for the transaction, and the transaction is still usable.
        (await SettingAsync(scope, "jit")).Should().Be(jit);
        (await ReadAsync(scope, rows)).Should().Equal(before);

        // The one sanctioned act on the same transaction then runs, and leaves the setting as it found it.
        (await RedactAsync(scope, plan)).RowsSelected.Should().BeGreaterThan(0);
        (await SettingAsync(scope, "jit")).Should().Be(jit);
        await scope.Transaction.RollbackAsync();
    }

    [Fact]
    public async Task A_connection_of_another_role_is_refused_before_anything_is_read()
    {
        var tenant = Guid.CreateVersion7();
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();

        await FluentActions.Awaiting(() => Executor().RedactAsync(Plan(tenant, DataRequestOrigin.Self, null),
                Spent(PlatformOperations.ErasureComplete), app, transaction))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*platform role*", "learnstack_app sees only what its row policy shows and holds no UPDATE");
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task The_platform_role_still_cannot_change_any_column_outside_the_grant()
    {
        var tenant = Guid.CreateVersion7();
        var rows = Reach(tenant, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await using var scope = await OpenAsync(schema.Postgres.PlatformConnectionString);
        await InsertAsync(scope, rows);
        await RedactAsync(scope, Plan(tenant, DataRequestOrigin.Self, null));
        var target = rows[0].Id;

        foreach (var statement in new[]
        {
            "UPDATE audit_log SET metadata = '{}'::jsonb WHERE id = @id",
            "UPDATE audit_log SET actor_user_id = NULL WHERE id = @id",
            "UPDATE audit_log SET entity_id = 'x', changes = NULL WHERE id = @id",
        })
        {
            var refusal = await DataExportTestKit.RefuseAsync(scope.Transaction, async () =>
            {
                await using var command = new NpgsqlCommand(statement, scope.Connection, scope.Transaction);
                command.Parameters.AddWithValue("id", target);
                await command.ExecuteNonQueryAsync();
            });
            refusal.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, statement);
        }

        await scope.Transaction.RollbackAsync();
    }

    private static IReadOnlyList<IUserReferenceLocator> RegisteredLocators()
    {
        var services = new ServiceCollection();
        services.AddIdentityContextualFoundation();
        services.AddDataExportCustody(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        return [.. provider.GetServices<IUserReferenceLocator>()];
    }

    private static AuditRedactionExecutor Executor() => new(Registered.Value);

    private static string ModuleOf(IUserReferenceLocator locator) => ModuleOf(locator.GetType());

    private static string ModuleOf(Type type)
    {
        var name = type.Assembly.GetName().Name!;
        const string prefix = "LearnStack.Modules.";
        return name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..name.IndexOf('.', prefix.Length)] : name;
    }

    private static PlatformAdmission Spent(PlatformOperation operation)
    {
        var admission = TestPlatformAdmissions.Admission(operation);
        admission.TryConsume().Should().BeTrue("the scope spends the admission on entry");
        return admission;
    }

    private static AuditRedactionPlan Plan(Guid tenant, DataRequestOrigin origin, Guid? organization) =>
        new(new UserDataSubject(Guid.CreateVersion7(), DataRequestKind.Erasure, origin, UserId.From(Subject),
            new DataRequestScopeGroup(TenantId.From(tenant), organization is { } org ? OrganizationId.From(org) : null),
            [Membership1, Membership2]));

    private static AuditRedactionPlan ClosurePlan(Guid tenant) =>
        new(new UserDataSubject(Guid.CreateVersion7(), DataRequestKind.Closure, DataRequestOrigin.Self, UserId.From(Subject),
            new DataRequestScopeGroup(TenantId.From(tenant), null), [Membership1]));

    private static async Task<AuditRedactionReport> RedactAsync(Scope scope, AuditRedactionPlan plan) =>
        await Executor().RedactAsync(plan, Spent(PlatformOperations.ErasureComplete), scope.Connection, scope.Transaction);

    private static Dictionary<RetainedReferenceKind, long> Retained(AuditRedactionReport report) =>
        report.Retained.ToDictionary(entry => entry.Kind, entry => entry.Count);

    private static async Task<string> SettingAsync(Scope scope, string name)
    {
        await using var command = new NpgsqlCommand("SELECT current_setting(@name)", scope.Connection, scope.Transaction);
        command.Parameters.AddWithValue("name", name);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// The three canaries of one declaration: the subject's identifier of the declaration's kind at the path, beside
    /// another person's at the same path; only other people's; and the subject's identifier of the other kind.
    /// </summary>
    private static IEnumerable<CanaryRow> Canary(UserReferenceDeclaration declaration, Guid tenant)
    {
        var label = declaration.EntityName + "." + declaration.Path;
        var own = Text(declaration.Kind == UserReferenceKind.User ? Subject : Membership1);
        var other = Text(declaration.Kind == UserReferenceKind.User ? OtherPerson : OtherMembership);
        var wrongKind = Text(declaration.Kind == UserReferenceKind.User ? Membership2 : Subject);
        var sentinel = SensitiveTokenCatalog.RedactedValue;

        var entity = Guid.CreateVersion7().ToString("D");
        var (before, after, changes) = Payload(declaration, entity, own, own.ToUpperInvariant(), other, own);
        var (expectedBefore, expectedAfter, expectedChanges) = Payload(declaration, entity, sentinel, sentinel, other, own);
        yield return new CanaryRow(label + " (subject)", tenant, null, ThirdPerson, declaration.EntityName, entity,
            before, after, changes, null, Selected: true, expectedBefore, expectedAfter, expectedChanges);

        var twin = Guid.CreateVersion7().ToString("D");
        var third = Text(declaration.Kind == UserReferenceKind.User ? ThirdPerson : Guid.CreateVersion7());
        (before, after, changes) = Payload(declaration, twin, third, third.ToUpperInvariant(), other, third);
        yield return new CanaryRow(label + " (others only)", tenant, null, ThirdPerson, declaration.EntityName, twin,
            before, after, changes, null, Selected: false, before, after, changes);

        var mismatch = Guid.CreateVersion7().ToString("D");
        (before, after, changes) = Payload(declaration, mismatch, wrongKind, wrongKind.ToUpperInvariant(), other, wrongKind);
        yield return new CanaryRow(label + " (other kind)", tenant, null, ThirdPerson, declaration.EntityName, mismatch,
            before, after, changes, null, Selected: false, before, after, changes);
    }

    /// <summary>
    /// The capture's shape for one declared path: <c>own</c> where the path points in each column, <c>other</c> at the
    /// same path in the other slot, and <c>remark</c> under a property no locator declares, which nothing may redact.
    /// </summary>
    private static (JsonNode Before, JsonNode After, JsonNode Changes) Payload(UserReferenceDeclaration declaration,
        string entity, string own, string ownUpper, string other, string remark)
    {
        var path = declaration.Path;
        var type = declaration.EntityName;
        switch (path.Shape)
        {
            case UserReferenceShape.Property:
                return (State(entity, (path.Property, own), ("Remark", remark)), State(entity, (path.Property, own), ("Remark", remark)),
                    new JsonArray(Item(type, entity, path.Property, other, own), Item(type, entity, "Remark", remark, remark)));
            case UserReferenceShape.Member:
                JsonObject Members() => new()
                {
                    ["k1"] = new JsonObject { [path.Property] = own, ["Remark"] = remark },
                    ["b/2"] = new JsonObject { [path.Property] = other },
                };
                return (State(entity, (path.Navigation!, Members())), State(entity, (path.Navigation!, Members())),
                    new JsonArray(Item(type, entity, path.Navigation + "/k1/" + path.Property, other, own),
                        Item(type, entity, path.Navigation + "/b~12/" + path.Property, other, other),
                        Item(type, entity, path.Navigation + "/k1/Remark", remark, remark)));
            case UserReferenceShape.Elements:
                return (State(entity, (path.Property, new JsonArray(own, other, ownUpper))),
                    State(entity, (path.Property, new JsonArray(other, own))),
                    new JsonArray(Item(type, entity, path.Property, new JsonArray(other), new JsonArray(own, other))));
            default:
                throw new NotSupportedException("A path shape the canary cannot build is a path no canary proves: " + path);
        }
    }

    private static JsonObject State(string entity, params (string Name, JsonNode? Value)[] members)
    {
        var state = new JsonObject { ["Id"] = entity, ["Title"] = "canary" };
        foreach (var (name, value) in members)
        {
            state[name] = value;
        }

        return state;
    }

    private static JsonObject Item(string type, string entity, string rest, JsonNode? before, JsonNode? after) =>
        new() { ["path"] = "/" + type + "/" + entity + "/" + rest, ["before"] = before, ["after"] = after };

    private static string Text(Guid value) => value.ToString("D");
}
