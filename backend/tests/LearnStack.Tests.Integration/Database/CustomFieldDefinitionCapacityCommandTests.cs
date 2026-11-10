using System.Globalization;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;
using static LearnStack.Tests.Integration.Database.MembershipCustomFieldFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class CustomFieldDefinitionCapacityCommandTests(MembershipCustomFieldFixture fixture)
    : IClassFixture<MembershipCustomFieldFixture>
{
    [Fact]
    public async Task Actual_commands_enforce_Draft_Active_concept_and_retained_revision_edges_and_preserve_recovery()
    {
        var drafts = new List<CustomFieldDefinitionDto>();
        for (var index = 0; index < 20; index++)
        {
            var draft = await fixture.Definitions.SendAsync(new RegisterCustomFieldDefinitionCommand(Guid.CreateVersion7(),
                Input("draft-cap-" + index.ToString(CultureInfo.InvariantCulture))), "capacity-draft-pass");
            Assert.True(draft.IsSuccess, draft.Error?.Code);
            drafts.Add(draft.Value);
        }
        var overflowId = Guid.CreateVersion7();
        var overflowInput = Input("capacity-overflow");
        Assert.Equal("limit_exceeded", (await fixture.Definitions.SendAsync(new RegisterCustomFieldDefinitionCommand(overflowId,
            overflowInput), "capacity-draft-refuse")).Error!.Code);
        foreach (var draft in drafts)
            Assert.True((await fixture.Definitions.SendAsync(new DiscardCustomFieldDefinitionDraftCommand(draft.DefinitionId,
                draft.Version), "capacity-draft-discard")).IsSuccess);
        // Discard releases Draft capacity, while those retained concept and
        // version reservations remain part of the later durable totals.
        var active = new List<CustomFieldDefinitionDto>();
        for (var index = 0; index < 20; index++)
            active.Add(await fixture.PublishAsync(Input("active-cap-" + index.ToString(CultureInfo.InvariantCulture))));
        var overflow = await fixture.Definitions.SendAsync(new RegisterCustomFieldDefinitionCommand(overflowId, overflowInput), "capacity-draft-released");
        Assert.True(overflow.IsSuccess, overflow.Error?.Code);
        Assert.Equal("limit_exceeded", (await fixture.Definitions.SendAsync(new PublishCustomFieldDefinitionCommand(overflowId,
            overflow.Value.Version, null, null), "capacity-active-refuse")).Error!.Code);
        await fixture.RetireAsync(active[0]);
        Assert.True((await fixture.Definitions.SendAsync(new PublishCustomFieldDefinitionCommand(overflowId,
            overflow.Value.Version, null, null), "capacity-active-released")).IsSuccess);

        // Real retained history is expensive to author individually. Only the
        // historical setup is raw: boundary acts below use the actual audited
        // command, its exclusive fence, EF usage reader and admission checks.
        await SeedRetainedAsync(concepts: 99, revisions: 99);
        var lastConcept = await fixture.Definitions.SendAsync(new RegisterCustomFieldDefinitionCommand(Guid.CreateVersion7(),
            Input("last-concept")), "capacity-concept-pass");
        Assert.True(lastConcept.IsSuccess, lastConcept.Error?.Code);
        Assert.Equal("limit_exceeded", (await fixture.Definitions.SendAsync(new RegisterCustomFieldDefinitionCommand(Guid.CreateVersion7(),
            Input("concept-overflow")), "capacity-concept-refuse")).Error!.Code);
        Assert.True((await fixture.Definitions.SendAsync(new DiscardCustomFieldDefinitionDraftCommand(lastConcept.Value.DefinitionId,
            lastConcept.Value.Version), "capacity-last-concept-discard")).IsSuccess);
        // Additional revisions of an already-reserved key remain permitted at
        // the concept cap, until the independent retained revision cap binds.
        await SeedRetainedAsync(concepts: 100, revisions: 999);
        var lastRevision = await fixture.Definitions.SendAsync(new RegisterCustomFieldDefinitionCommand(Guid.CreateVersion7(),
            Input("last-concept", version: 2)), "capacity-revision-pass");
        Assert.True(lastRevision.IsSuccess, lastRevision.Error?.Code);
        Assert.Equal("limit_exceeded", (await fixture.Definitions.SendAsync(new RegisterCustomFieldDefinitionCommand(Guid.CreateVersion7(),
            Input("last-concept", version: 3)), "capacity-revision-refuse")).Error!.Code);
        var edited = await fixture.Definitions.SendAsync(new ReviseCustomFieldDefinitionDraftCommand(lastRevision.Value.DefinitionId,
            lastRevision.Value.Version, Input("last-concept", version: 2) with { Sort = 1 }), "capacity-existing-draft-edit");
        Assert.True(edited.IsSuccess, edited.Error?.Code);
        await fixture.RetireAsync(active[1]);
        var published = await fixture.Definitions.SendAsync(new PublishCustomFieldDefinitionCommand(edited.Value.DefinitionId,
            edited.Value.Version, null, null), "capacity-existing-draft-publish");
        Assert.True(published.IsSuccess, published.Error?.Code);
        await fixture.RetireAsync(published.Value);
    }

    private Task SeedRetainedAsync(int concepts, int revisions) => fixture.Membership.MutateAsync(SchemaFixture.TenantA,
        null, null, true, async (app, transaction) =>
        {
            var currentConcepts = await ScalarAsync<long>(app, transaction, "SELECT count(DISTINCT key) FROM tenant_custom_field_defs");
            var currentRevisions = await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM tenant_custom_field_defs");
            var schema = Input("unused").JsonSchema;
            for (var index = currentRevisions; index < revisions; index++)
            {
                var addConcept = currentConcepts < concepts;
                var key = addConcept ? "retained-" + (currentConcepts++).ToString(CultureInfo.InvariantCulture) : "retained-41";
                var version = addConcept ? 1 : checked((int)await ScalarAsync<long>(app, transaction,
                    "SELECT coalesce(max(schema_version),0)::bigint+1 FROM tenant_custom_field_defs WHERE key=@key", ("key", key)));
                var id = Guid.CreateVersion7();
                await ExecuteAsync(app, transaction, """
                    INSERT INTO tenant_custom_field_defs
                      (id,tenant_id,target_entity,key,schema_version,status,json_schema,labels_json,pii_category,
                       is_required,sort,maximum_value_bytes,created_at,created_by,row_version)
                    VALUES(@id,@tenant,'Membership',@key,@version,'Draft',@schema::jsonb,'{"en":"Retained history"}',
                       'None',false,0,5,clock_timestamp(),@actor,0);
                    UPDATE tenant_custom_field_defs SET deleted_at=clock_timestamp(),deleted_by=@actor,row_version=1 WHERE id=@id;
                    """, ("id", id), ("tenant", SchemaFixture.TenantA), ("key", key), ("version", version),
                    ("schema", schema), ("actor", AdminA));
            }
            Assert.Equal((long)concepts, await ScalarAsync<long>(app, transaction, "SELECT count(DISTINCT key) FROM tenant_custom_field_defs"));
            Assert.Equal((long)revisions, await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM tenant_custom_field_defs"));
        });
}
