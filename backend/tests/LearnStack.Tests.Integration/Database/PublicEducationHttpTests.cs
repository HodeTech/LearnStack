using System.Net;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace LearnStack.Tests.Integration.Database;

[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class PublicEducationHttpTests(PublicReadFixture fixture, ITestOutputHelper output)
{
    private const string Root = "/api/v1/public/courses";
    private static SeedTenant English => SeedData.English;
    private static SeedTenant Yoga => SeedData.Yoga;

    [Fact]
    public async Task Catalog_is_exact_locale_host_scoped_and_only_marketing_with_no_normal_audit()
    {
        var beforeEnglish = await fixture.AuditCountAsync(English);
        var beforeYoga = await fixture.AuditCountAsync(Yoga);
        using var english = fixture.ClientFor(English.Host);
        var catalog = await Json(english, Root + "?locale=EN");
        Names(catalog, "locale", "items", "pageInfo");
        catalog.GetProperty("locale").GetString().Should().Be("en");
        Slugs(catalog.GetProperty("items")).Should().Equal("foundation", "guided-practice");
        foreach (var item in catalog.GetProperty("items").EnumerateArray())
        {
            Names(item, "slug", "title", "summary", "contentAccess", "level");
            Names(item.GetProperty("level"), "state", "label");
            item.GetProperty("level").GetProperty("state").GetString().Should().Be("ready");
            Names(item.GetProperty("level").GetProperty("label"), "value", "locale");
            item.GetProperty("level").GetProperty("label").GetProperty("locale").GetString().Should().Be("en");
        }
        Terminal(catalog.GetProperty("pageInfo"));
        using var yoga = fixture.ClientFor(Yoga.Host);
        var other = await Json(yoga, Root + "?locale=en");
        Slugs(other.GetProperty("items")).Should().Equal("shared-practice", "foundation", "guided-flow");
        Slugs((await Json(yoga, Root + "?locale=TR-tr")).GetProperty("items")).Should().Equal("ortak-pratik", "studyo-temelleri", "rehberli-akis");
        (await fixture.AuditCountAsync(English)).Should().Be(beforeEnglish);
        (await fixture.AuditCountAsync(Yoga)).Should().Be(beforeYoga);
        catalog.GetRawText().Should().NotContain(English.TenantId.Value.ToString("D")).And.NotContain("schemaVersion").And.NotContain("generation");
    }

    [Fact]
    public async Task Course_and_lesson_use_exact_translations_and_real_alternate_url_pairs()
    {
        using var client = fixture.ClientFor(Yoga.Host);
        var course = await Json(client, Root + "/foundation?locale=en");
        Names(course, "locale", "course", "alternates", "lessons");
        course.GetProperty("course").GetProperty("title").GetString().Should().Be(Yoga.Curriculum!.Courses[1].Translations.Single(row => row.Locale == "en").Title);
        course.GetProperty("alternates").EnumerateArray().Should().ContainSingle().Which.GetProperty("slug").GetString().Should().Be("studyo-temelleri");
        Slugs(course.GetProperty("lessons").GetProperty("items")).Should().Equal("tree-pose", "child-pose");
        Terminal(course.GetProperty("lessons").GetProperty("pageInfo"));
        var lesson = await Json(client, Root + "/foundation/lessons/tree-pose?locale=en");
        Names(lesson, "locale", "course", "lesson", "alternates", "content");
        Names(lesson.GetProperty("course"), "slug", "title");
        Names(lesson.GetProperty("lesson"), "slug", "title");
        var alternate = lesson.GetProperty("alternates").EnumerateArray().Single();
        Names(alternate, "locale", "courseSlug", "lessonSlug");
        alternate.GetProperty("locale").GetString().Should().Be("tr-TR");
        alternate.GetProperty("courseSlug").GetString().Should().Be("studyo-temelleri");
        alternate.GetProperty("lessonSlug").GetString().Should().Be("agac-durusu");
        var content = lesson.GetProperty("content");
        Names(content, "state", "rendererKey", "label", "fields");
        content.GetProperty("state").GetString().Should().Be("ready");
        content.GetProperty("rendererKey").GetString().Should().Be("default-card");
        var fields = content.GetProperty("fields").EnumerateArray().ToArray();
        fields.Select(field => field.GetProperty("name").GetString()).Should().Equal("pose", "instruction", "breathing");
        foreach (var field in fields) { Names(field, "name", "label", "value"); Names(field.GetProperty("label"), "value", "locale"); }
        var translated = await Json(client, Root + "/studyo-temelleri/lessons/agac-durusu?locale=tr-TR");
        translated.GetProperty("content").GetProperty("fields")[0].GetProperty("value").GetString().Should()
            .NotBe(fields[0].GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("?locale=en_US", "locale")]
    [InlineData("", "locale")]
    [InlineData("?locale=", "locale")]
    [InlineData("?locale=%20en", "locale")]
    [InlineData("?locale=en&locale=en", "locale")]
    [InlineData("?locale=en&limit=0", "limit")]
    [InlineData("?locale=en&limit=-1", "limit")]
    [InlineData("?locale=en&limit=1&limit=2", "limit")]
    [InlineData("?locale=en&cursor=%3D", "cursor")]
    [InlineData("?locale=en&cursor=x&cursor=x", "cursor")]
    [InlineData("?locale=en&tenantId=x", "query")]
    [InlineData("?locale=en&Locale=en", "query")]
    public async Task Query_shape_refusals_happen_before_configuration_or_data_read(string query, string field)
    {
        using var client = fixture.ClientFor(English.Host);
        var before = fixture.Observation.Reads;
        using var response = await client.GetAsync(Root + query);
        var error = await Error(response, HttpStatusCode.BadRequest, "validation_failed");
        error.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue();
        fixture.Observation.Reads.Should().Be(before);
    }

    [Theory]
    [InlineData(false, "utf8")]
    [InlineData(false, "value")]
    [InlineData(false, "name")]
    [InlineData(true, "utf8")]
    [InlineData(true, "value")]
    [InlineData(true, "name")]
    public async Task Malformed_cursor_unicode_is_a_bounded_400_before_data_access(bool outline, string corruption)
    {
        var scope = new string('a', 64);
        var json = outline
            ? $"{{\"v\":1,\"scope\":\"{scope}\",\"parent\":\"01930000-0000-7000-8000-000000000e11\",\"sort\":0,\"id\":\"01930000-0000-7000-8000-000000000e21\"}}"
            : $"{{\"v\":1,\"scope\":\"{scope}\",\"createdAt\":\"2026-10-03T12:34:56.123456Z\",\"id\":\"01930000-0000-7000-8000-000000000e11\"}}";
        if (corruption == "value") json = json.Replace(scope, "\\uD800", StringComparison.Ordinal);
        if (corruption == "name") json = json.Replace("\"v\"", "\"\\uD800\"", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(json);
        if (corruption == "utf8") bytes[json.IndexOf(scope, StringComparison.Ordinal)] = 0xff;
        var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var field = outline ? "lessonCursor" : "cursor";
        var path = Root + (outline ? "/foundation" : "") + "?locale=en&" + field + "=" + token;
        using var client = fixture.ClientFor(English.Host);
        var before = fixture.Observation.Reads;
        using var response = await client.GetAsync(path);
        (await Error(response, HttpStatusCode.BadRequest, "validation_failed")).GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue();
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
        head.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        NoStore(head);
        (await head.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        fixture.Observation.Reads.Should().Be(before);
    }

    [Theory]
    [InlineData("/UPPER?locale=en", "slug")]
    [InlineData("/01930000-0000-7000-8000-000000000e11?locale=en", "slug")]
    [InlineData("/foundation?locale=en&lessonLimit=0", "lessonLimit")]
    [InlineData("/foundation?locale=en&lessonCursor=bad", "lessonCursor")]
    [InlineData("/foundation/lessons/UPPER?locale=en", "lessonSlug")]
    [InlineData("/foundation/lessons/present-simple?locale=en&limit=1", "query")]
    public async Task Detail_validation_does_not_enter_read_transaction(string path, string field)
    {
        using var client = fixture.ClientFor(English.Host);
        var before = fixture.Observation.Reads;
        using var response = await client.GetAsync(Root + path);
        var error = await Error(response, HttpStatusCode.BadRequest, "validation_failed");
        error.GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue();
        fixture.Observation.Reads.Should().Be(before);
    }

    [Theory]
    [InlineData("?locale=fr")]
    [InlineData("/foundation?locale=fr")]
    [InlineData("/foundation/lessons/present-simple?locale=fr")]
    public async Task Well_formed_unsupported_locale_is_400_and_headers_do_not_replace_explicit_locale(string path)
    {
        using var client = fixture.ClientFor(English.Host);
        client.DefaultRequestHeaders.Add("Accept-Language", "en");
        client.DefaultRequestHeaders.Add("X-Locale", "en");
        using var response = await client.GetAsync(Root + path);
        await Error(response, HttpStatusCode.BadRequest, "unsupported_locale");
        using var missing = await client.GetAsync(Root);
        await Error(missing, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Theory]
    [InlineData("next-steps")]
    [InlineData("branch-conversation")]
    [InlineData("shared-practice")]
    [InlineData("missing")]
    public async Task Course_draft_branch_foreign_and_unknown_are_uniformly_hidden(string slug)
    {
        using var client = fixture.ClientFor(English.Host);
        using var response = await client.GetAsync(Root + "/" + slug + "?locale=en");
        await Error(response, HttpStatusCode.NotFound, "not_found");
    }

    [Theory]
    [InlineData("foundation", "questions")]
    [InlineData("next-steps", "past-simple")]
    [InlineData("guided-practice", "giving-reasons")]
    [InlineData("branch-conversation", "introductions")]
    [InlineData("foundation", "giving-reasons")]
    [InlineData("foundation", "tree-pose")]
    [InlineData("missing", "present-simple")]
    public async Task Lesson_masks_draft_parent_draft_restricted_scope_wrong_parent_and_foreign_rows(string course, string lesson)
    {
        using var client = fixture.ClientFor(English.Host);
        using var response = await client.GetAsync($"{Root}/{course}/lessons/{lesson}?locale=en");
        await Error(response, HttpStatusCode.NotFound, "not_found");
    }

    [Theory]
    [InlineData("/studio-flow?locale=en")]
    [InlineData("/studyo-akisi?locale=tr-TR")]
    [InlineData("/studio-flow/lessons/warrior-pose?locale=en")]
    [InlineData("/foundation/lessons/warrior-pose?locale=en")]
    public async Task Organization_host_never_serves_a_sibling_organization(string path)
    {
        using var client = fixture.ClientFor(Yoga.Host);
        using var response = await client.GetAsync(Root + path);
        await Error(response, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Absent_level_and_nullable_summary_stay_null_in_the_actual_wire_contract()
    {
        using var client = fixture.ClientFor(English.Host);
        var course = English.Curriculum!.Courses[0];
        var parameters = new Dictionary<string, object> { ["id"] = course.Id };
        await fixture.ExecuteAsync(English, "UPDATE courses SET level_taxonomy_key=NULL,level_taxonomy_schema_version=NULL,level_band_key=NULL WHERE tenant_id=@tenant AND id=@id", parameters);
        await fixture.ExecuteOwnerFixtureAsync(English, "UPDATE course_translations SET summary=NULL WHERE tenant_id=@tenant AND course_id=@id AND locale='en'", parameters);
        try
        {
            var detail = (await Json(client, Root + "/foundation?locale=en")).GetProperty("course");
            detail.GetProperty("level").ValueKind.Should().Be(JsonValueKind.Null);
            detail.GetProperty("summary").ValueKind.Should().Be(JsonValueKind.Null);
        }
        finally
        {
            parameters["summary"] = course.Translations.Single(row => row.Locale == "en").Summary!;
            parameters["key"] = course.LevelTaxonomyKey;
            parameters["version"] = course.LevelTaxonomySchemaVersion;
            parameters["band"] = course.LevelBandKey;
            await fixture.ExecuteOwnerFixtureAsync(English, "UPDATE course_translations SET summary=@summary WHERE tenant_id=@tenant AND course_id=@id AND locale='en'", parameters);
            await fixture.ExecuteAsync(English, "UPDATE courses SET level_taxonomy_key=@key,level_taxonomy_schema_version=@version,level_band_key=@band WHERE tenant_id=@tenant AND id=@id", parameters);
        }
    }

    [Fact]
    public async Task Restricted_course_has_marketing_but_no_inventory_and_nonexistent_lesson_is_same_refusal()
    {
        using var client = fixture.ClientFor(English.Host);
        var course = await Json(client, Root + "/guided-practice?locale=en");
        course.GetProperty("course").GetProperty("contentAccess").GetString().Should().Be("enrollment_required");
        course.GetProperty("lessons").ValueKind.Should().Be(JsonValueKind.Null);
        course.GetRawText().Should().NotContain("giving-reasons").And.NotContain("contentType");
        foreach (var slug in new[] { "giving-reasons", "missing" })
        {
            using var response = await client.GetAsync(Root + "/guided-practice/lessons/" + slug + "?locale=en");
            await Error(response, HttpStatusCode.NotFound, "not_found");
        }
    }

    [Theory]
    [InlineData("course-deleted")]
    [InlineData("course-draft")]
    [InlineData("course-restricted")]
    [InlineData("lesson-deleted")]
    [InlineData("lesson-draft")]
    public async Task Source_eligibility_is_fresh_after_success_and_warmed_definition_cache(string change)
    {
        using var client = fixture.ClientFor(English.Host);
        var path = Root + "/foundation/lessons/present-simple?locale=en";
        (await Json(client, path)).GetProperty("content").GetProperty("state").GetString().Should().Be("ready");
        var sql = change switch
        {
            "course-deleted" => "UPDATE courses SET deleted_at=now(),deleted_by=@actor WHERE tenant_id=@tenant AND slug_key='foundations'",
            "course-draft" => "UPDATE courses SET status='draft' WHERE tenant_id=@tenant AND slug_key='foundations'",
            "course-restricted" => "UPDATE courses SET content_access='enrollment_required' WHERE tenant_id=@tenant AND slug_key='foundations'",
            "lesson-deleted" => "UPDATE lessons SET deleted_at=now(),deleted_by=@actor WHERE tenant_id=@tenant AND id='01930000-0000-7000-8000-000000000e21'",
            _ => "UPDATE lessons SET status='draft' WHERE tenant_id=@tenant AND id='01930000-0000-7000-8000-000000000e21'",
        };
        await fixture.ExecuteAsync(English, sql);
        try
        {
            using var response = await client.GetAsync(path);
            await Error(response, HttpStatusCode.NotFound, "not_found");
        }
        finally
        {
            await fixture.ExecuteAsync(English, """
                UPDATE courses SET deleted_at=NULL,deleted_by=NULL,status='published',content_access='public'
                WHERE tenant_id=@tenant AND slug_key='foundations';
                UPDATE lessons SET deleted_at=NULL,deleted_by=NULL,status='published'
                WHERE tenant_id=@tenant AND id='01930000-0000-7000-8000-000000000e21'
                """);
        }
    }

    [Fact]
    public async Task Catalog_and_outline_seek_pages_are_forward_only_and_binding_survives_limit_changes()
    {
        using var client = fixture.ClientFor(Yoga.Host);
        var first = await Json(client, Root + "?locale=en&limit=1");
        var cursor = first.GetProperty("pageInfo").GetProperty("nextCursor").GetString()!;
        first.GetProperty("pageInfo").GetProperty("hasNext").GetBoolean().Should().BeTrue();
        var last = await Json(client, Root + "?locale=en&limit=100&cursor=" + cursor);
        Slugs(first.GetProperty("items")).Concat(Slugs(last.GetProperty("items"))).Should().Equal("shared-practice", "foundation", "guided-flow");
        Terminal(last.GetProperty("pageInfo"));
        var outline = await Json(client, Root + "/foundation?locale=en&lessonLimit=1");
        var childCursor = outline.GetProperty("lessons").GetProperty("pageInfo").GetProperty("nextCursor").GetString()!;
        var remaining = await Json(client, Root + "/foundation?locale=en&lessonCursor=" + childCursor + "&lessonLimit=2");
        Slugs(remaining.GetProperty("lessons").GetProperty("items")).Should().Equal("child-pose");
        Terminal(remaining.GetProperty("lessons").GetProperty("pageInfo"));
        foreach (var path in new[] { Root + "?locale=tr-TR&cursor=" + cursor,
                     Root + "/foundation?locale=en&lessonCursor=" + cursor,
                     Root + "/shared-practice?locale=en&lessonCursor=" + childCursor })
        {
            using var response = await client.GetAsync(path);
            await Error(response, HttpStatusCode.BadRequest, "validation_failed");
        }
        using var foreign = fixture.ClientFor(English.Host);
        using var cross = await foreign.GetAsync(Root + "?locale=en&cursor=" + cursor);
        await Error(cross, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Deleted_catalog_and_outline_anchors_continue_by_seek_and_restart_uses_current_rows()
    {
        using var client = fixture.ClientFor(Yoga.Host);
        var catalogCursor = (await Json(client, Root + "?locale=en&limit=1")).GetProperty("pageInfo").GetProperty("nextCursor").GetString()!;
        var outlineCursor = (await Json(client, Root + "/foundation?locale=en&lessonLimit=1")).GetProperty("lessons").GetProperty("pageInfo").GetProperty("nextCursor").GetString()!;
        var course = new Dictionary<string, object> { ["id"] = Yoga.Curriculum!.Courses[0].Id };
        var lesson = new Dictionary<string, object> { ["id"] = Yoga.Curriculum.Courses.SelectMany(row => row.Lessons).Single(row => row.Translations.Any(translation => translation.Slug == "tree-pose")).Id };
        await fixture.ExecuteAsync(Yoga, "UPDATE courses SET deleted_at=now(),deleted_by=@actor WHERE tenant_id=@tenant AND id=@id", course);
        try
        {
            await fixture.ExecuteAsync(Yoga, "UPDATE lessons SET deleted_at=now(),deleted_by=@actor WHERE tenant_id=@tenant AND id=@id", lesson, Yoga.DefaultOrganization.OrganizationId);
            try
            {
                var remaining = await Json(client, Root + "?locale=en&cursor=" + catalogCursor);
                Slugs(remaining.GetProperty("items")).Should().Equal("foundation", "guided-flow");
                Terminal(remaining.GetProperty("pageInfo"));
                Slugs((await Json(client, Root + "?locale=en")).GetProperty("items")).Should().Equal("foundation", "guided-flow");
                var outline = (await Json(client, Root + "/foundation?locale=en&lessonCursor=" + outlineCursor)).GetProperty("lessons");
                Slugs(outline.GetProperty("items")).Should().Equal("child-pose");
                Terminal(outline.GetProperty("pageInfo"));
                Slugs((await Json(client, Root + "/foundation?locale=en")).GetProperty("lessons").GetProperty("items")).Should().Equal("child-pose");
            }
            finally { await fixture.ExecuteAsync(Yoga, "UPDATE lessons SET deleted_at=NULL,deleted_by=NULL WHERE tenant_id=@tenant AND id=@id", lesson, Yoga.DefaultOrganization.OrganizationId); }
        }
        finally { await fixture.ExecuteAsync(Yoga, "UPDATE courses SET deleted_at=NULL,deleted_by=NULL WHERE tenant_id=@tenant AND id=@id", course); }
    }

    [Theory]
    [InlineData("course_translations", "course_id", "01930000-0000-7000-8000-000000000f12")]
    [InlineData("lesson_translations", "lesson_id", "01930000-0000-7000-8000-000000000f22")]
    public async Task Missing_exact_translation_never_uses_default_content_or_advertises_disabled_alternates(string table, string parent, string id)
    {
        using var client = fixture.ClientFor(Yoga.Host);
        // Fixed theory literals form SQL identifiers; row values remain parameters.
        var parameters = new Dictionary<string, object> { ["id"] = Guid.Parse(id) };
        await fixture.ExecuteOwnerFixtureAsync(Yoga, $"UPDATE {table} SET locale='fr' WHERE tenant_id=@tenant AND {parent}=@id AND locale='en'", parameters, Yoga.DefaultOrganization.OrganizationId);
        try
        {
            using var response = await client.GetAsync(Root + "/foundation/lessons/tree-pose?locale=en");
            await Error(response, HttpStatusCode.NotFound, "not_found");
            var translated = await Json(client, Root + "/studyo-temelleri/lessons/agac-durusu?locale=tr-TR");
            translated.GetProperty("alternates").GetArrayLength().Should().Be(0);
            if (table == "course_translations")
            {
                Slugs((await Json(client, Root + "?locale=en")).GetProperty("items")).Should().NotContain("foundation");
                using var course = await client.GetAsync(Root + "/foundation?locale=en");
                await Error(course, HttpStatusCode.NotFound, "not_found");
            }
            else Slugs((await Json(client, Root + "/foundation?locale=en")).GetProperty("lessons").GetProperty("items")).Should().Equal("child-pose");
        }
        finally { await fixture.ExecuteOwnerFixtureAsync(Yoga, $"UPDATE {table} SET locale='en' WHERE tenant_id=@tenant AND {parent}=@id AND locale='fr'", parameters, Yoga.DefaultOrganization.OrganizationId); }
    }

    [Fact]
    public async Task Enabled_untranslated_locale_returns_empty_catalog_and_hidden_details_without_fallback()
    {
        using var client = fixture.ClientFor(English.Host);
        await fixture.ExecuteOwnerFixtureAsync(English, "UPDATE tenant_locales SET locale='fr' WHERE tenant_id=@tenant AND locale='en'");
        try
        {
            var catalog = await Json(client, Root + "?locale=fr");
            catalog.GetProperty("items").GetArrayLength().Should().Be(0);
            Terminal(catalog.GetProperty("pageInfo"));
            foreach (var path in new[] { "/foundation", "/foundation/lessons/present-simple" })
            {
                using var response = await client.GetAsync(Root + path + "?locale=fr");
                await Error(response, HttpStatusCode.NotFound, "not_found");
            }
        }
        finally { await fixture.ExecuteOwnerFixtureAsync(English, "UPDATE tenant_locales SET locale='en' WHERE tenant_id=@tenant AND locale='fr'"); }
    }

    [Fact]
    public async Task Disabled_locale_and_empty_enabled_set_are_unsupported_even_after_a_successful_read()
    {
        using var client = fixture.ClientFor(English.Host);
        await Json(client, Root + "?locale=en");
        await fixture.ExecuteAsync(English, "UPDATE tenant_locales SET is_enabled=false,is_default=false WHERE tenant_id=@tenant AND locale='en'");
        try
        {
            foreach (var path in new[] { "", "/foundation", "/foundation/lessons/present-simple" })
            {
                using var response = await client.GetAsync(Root + path + "?locale=en");
                await Error(response, HttpStatusCode.BadRequest, "unsupported_locale");
            }
        }
        finally { await fixture.ExecuteAsync(English, "UPDATE tenant_locales SET is_enabled=true,is_default=true WHERE tenant_id=@tenant AND locale='en'"); }
    }

    [Theory]
    [InlineData("{}", "unavailable")]
    [InlineData("{\"concept\":42,\"example\":\"safe\"}", "unavailable")]
    [InlineData("{\"concept\":\"plain\",\"example\":\"safe\",\"hidden\":\"secret\"}", "ready")]
    public async Task Out_of_band_body_shape_is_bounded_and_raw_or_unknown_payload_never_escapes(string body, string state)
    {
        using var client = fixture.ClientFor(English.Host);
        var lesson = English.Curriculum!.Courses[0].Lessons[0];
        var parameters = new Dictionary<string, object> { ["id"] = lesson.Id, ["body"] = body };
        await fixture.ExecuteOwnerFixtureAsync(English, "UPDATE lesson_translations SET body=CAST(@body AS jsonb) WHERE tenant_id=@tenant AND lesson_id=@id AND locale='en'", parameters);
        try
        {
            var result = await Json(client, Root + "/foundation/lessons/present-simple?locale=en");
            var content = result.GetProperty("content");
            content.GetProperty("state").GetString().Should().Be(state);
            if (state == "unavailable") Names(content, "state");
            else SlugsForFields(content).Should().Equal("concept", "example");
            content.GetRawText().Should().NotContain("secret").And.NotContain("body");
        }
        finally
        {
            parameters["body"] = lesson.Translations.Single(row => row.Locale == "en").Body;
            await fixture.ExecuteOwnerFixtureAsync(English, "UPDATE lesson_translations SET body=CAST(@body AS jsonb) WHERE tenant_id=@tenant AND lesson_id=@id AND locale='en'", parameters);
        }
    }

    [Theory]
    [InlineData("Draft", "unavailable")]
    [InlineData("Deprecated", "ready")]
    [InlineData("deleted", "unavailable")]
    public async Task Exact_definition_status_is_fresh_without_rebinding_and_marketing_survives_missing_taxonomy(string state, string expected)
    {
        using var client = fixture.ClientFor(English.Host);
        // Cold cache avoids relying on a raw fixture mutation to bump supported-write generation.
        await fixture.ExecuteOwnerFixtureAsync(English, state == "deleted"
            ? "UPDATE tenant_content_types SET deleted_at=now(),deleted_by=@actor WHERE tenant_id=@tenant AND key='grammar-topic'"
            : "UPDATE tenant_content_types SET status=@state WHERE tenant_id=@tenant AND key='grammar-topic'",
            new Dictionary<string, object> { ["state"] = state });
        try
        {
            var lesson = await Json(client, Root + "/foundation/lessons/present-simple?locale=en");
            lesson.GetProperty("content").GetProperty("state").GetString().Should().Be(expected);
            if (expected == "unavailable") Names(lesson.GetProperty("content"), "state");
            await fixture.ExecuteAsync(English, "UPDATE courses SET level_taxonomy_schema_version=999 WHERE tenant_id=@tenant AND slug_key='foundations'");
            var course = await Json(client, Root + "/foundation?locale=en");
            var level = course.GetProperty("course").GetProperty("level");
            Names(level, "state", "label");
            level.GetProperty("state").GetString().Should().Be("unavailable");
            level.GetProperty("label").ValueKind.Should().Be(JsonValueKind.Null);
            Slugs(course.GetProperty("lessons").GetProperty("items")).Should().Equal("present-simple");
        }
        finally
        {
            await fixture.ExecuteOwnerFixtureAsync(English, "UPDATE tenant_content_types SET status='Active',deleted_at=NULL,deleted_by=NULL WHERE tenant_id=@tenant AND key='grammar-topic'");
            await fixture.ExecuteAsync(English, "UPDATE courses SET level_taxonomy_schema_version=1 WHERE tenant_id=@tenant AND slug_key='foundations'");
        }
    }

    private static string[] SlugsForFields(JsonElement content) => content.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("name").GetString()!).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seeded_public_http_measurement_records_first_and_warm_requests_without_a_production_p95_claim(bool yoga)
    {
        var tenant = yoga ? Yoga : English;
        using var client = fixture.ClientFor(tenant.Host);
        var paths = new[] { Root + "?locale=en", Root + "/foundation?locale=en",
            Root + "/foundation/lessons/" + (yoga ? "tree-pose" : "present-simple") + "?locale=en" };
        var samples = new List<object>();
        foreach (var path in paths)
        {
            var first = await Measure(path);
            await Measure(path); // One explicit warmup after the first request.
            var warm = new List<double>();
            for (var iteration = 0; iteration < 10; iteration++) warm.Add((await Measure(path)).Milliseconds);
            var ordered = warm.Order().ToArray();
            samples.Add(new
            {
                Route = path,
                FirstRequestMs = first.Milliseconds,
                first.Utf8Bytes,
                WarmMs = new { Minimum = ordered[0], Median = (ordered[4] + ordered[5]) / 2, Maximum = ordered[^1] }
            });
        }
        output.WriteLine(JsonSerializer.Serialize(new { Sample = "local Docker PostgreSQL, production HTTP composition, first request then one warmup and ten observations per route; not production p95", Tenant = tenant.Slug, Samples = samples }));

        async Task<(double Milliseconds, int Utf8Bytes)> Measure(string path)
        {
            var started = Stopwatch.GetTimestamp();
            using var response = await client.GetAsync(path);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            NoStore(response);
            using var document = JsonDocument.Parse(bytes);
            document.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
            return (Stopwatch.GetElapsedTime(started).TotalMilliseconds, bytes.Length);
        }
    }

    [Theory]
    [InlineData("?locale=en", 200)]
    [InlineData("/foundation?locale=en", 200)]
    [InlineData("/foundation/lessons/present-simple?locale=en", 200)]
    [InlineData("/guided-practice/lessons/giving-reasons?locale=en", 404)]
    [InlineData("/foundation?locale=fr", 400)]
    [InlineData("?locale=en&limit=0", 400)]
    public async Task Get_head_and_conditional_requests_share_status_no_store_and_head_has_no_body(string path, int status)
    {
        using var client = fixture.ClientFor(English.Host);
        client.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match", "*");
        client.DefaultRequestHeaders.TryAddWithoutValidation("If-Modified-Since", "Wed, 21 Oct 2037 07:28:00 GMT");
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            using var response = await client.SendAsync(new HttpRequestMessage(method, Root + path));
            ((int)response.StatusCode).Should().Be(status);
            NoStore(response);
            if (method == HttpMethod.Head) (await response.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
        }
    }

    [Fact]
    public void Production_route_inventory_is_exactly_the_four_get_head_pairs()
    {
        var endpoints = fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("api/v1/public/", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        endpoints.Should().HaveCount(8);
        foreach (var route in endpoints.GroupBy(endpoint => endpoint.RoutePattern.RawText))
            route.SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods).Should().BeEquivalentTo(["GET", "HEAD"]);
        endpoints.Select(endpoint => endpoint.RoutePattern.RawText).Distinct().Should().BeEquivalentTo(
            ["api/v1/public/site", "api/v1/public/courses", "api/v1/public/courses/{slug}", "api/v1/public/courses/{slug}/lessons/{lessonSlug}"]);
    }

    private static async Task<JsonElement> Json(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        NoStore(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static async Task<JsonElement> Error(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status, await response.Content.ReadAsStringAsync());
        NoStore(response);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("code").GetString().Should().Be(code);
        if (code == "not_found")
        {
            Names(error, "type", "title", "status", "instance", "code", "messageKey", "correlationId");
            error.GetProperty("type").GetString().Should().Be("https://errors.learnstack.dev/not_found");
            error.GetProperty("title").GetString().Should().Be("lockey_not_found");
            error.GetProperty("messageKey").GetString().Should().Be("lockey_not_found");
            error.GetProperty("status").GetInt32().Should().Be(404);
            error.GetProperty("instance").GetString().Should().Be(response.RequestMessage!.RequestUri!.AbsolutePath);
            error.GetProperty("correlationId").GetString().Should().NotBeNullOrWhiteSpace();
        }
        return error;
    }
    private static string[] Slugs(JsonElement items) => items.EnumerateArray().Select(item => item.GetProperty("slug").GetString()!).ToArray();
    private static void Names(JsonElement value, params string[] names) => value.EnumerateObject().Select(member => member.Name).Should().BeEquivalentTo(names);
    private static void Terminal(JsonElement page)
    {
        Names(page, "nextCursor", "previousCursor", "hasNext", "hasPrevious");
        page.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
        page.GetProperty("previousCursor").ValueKind.Should().Be(JsonValueKind.Null);
        page.GetProperty("hasNext").GetBoolean().Should().BeFalse();
        page.GetProperty("hasPrevious").GetBoolean().Should().BeFalse();
    }
    private static void NoStore(HttpResponseMessage response)
    {
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
        response.Headers.ETag.Should().BeNull();
        response.Content.Headers.LastModified.Should().BeNull();
    }
}
