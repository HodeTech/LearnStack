using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Education.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class create_education_schema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "courses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    slug_key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    level_taxonomy_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    level_taxonomy_schema_version = table.Column<int>(type: "integer", nullable: true),
                    level_band_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    row_version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_courses", x => x.id);
                    table.UniqueConstraint("ux_courses_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_courses_level_reference", "(level_taxonomy_key IS NULL AND level_taxonomy_schema_version IS NULL AND level_band_key IS NULL)\nOR (level_taxonomy_key IS NOT NULL AND level_taxonomy_schema_version IS NOT NULL\n    AND level_taxonomy_schema_version > 0 AND level_band_key IS NOT NULL)");
                    table.CheckConstraint("ck_courses_slug_key_format", "slug_key ~ '^[a-z0-9]+(-[a-z0-9]+)*$' AND slug_key !~ '^[0-9a-f]{32}$' AND slug_key !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'");
                    table.CheckConstraint("ck_courses_status", "status IN ('draft', 'published')");
                });

            migrationBuilder.CreateTable(
                name: "course_translations",
                columns: table => new
                {
                    course_id = table.Column<Guid>(type: "uuid", nullable: false),
                    locale = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    summary = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_course_translations", x => new { x.course_id, x.locale });
                    table.UniqueConstraint("ux_course_translations_tenant_id_locale_slug", x => new { x.tenant_id, x.locale, x.slug });
                    table.CheckConstraint("ck_course_translations_slug_format", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$' AND slug !~ '^[0-9a-f]{32}$' AND slug !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'");
                    table.ForeignKey(
                        name: "fk_course_translations_course",
                        columns: x => new { x.tenant_id, x.course_id },
                        principalTable: "courses",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "lessons",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    course_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sort = table.Column<int>(type: "integer", nullable: false),
                    content_type_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    content_type_schema_version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    row_version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lessons", x => x.id);
                    table.UniqueConstraint("ux_lessons_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_lessons_content_type_schema_version", "content_type_schema_version > 0");
                    table.CheckConstraint("ck_lessons_sort", "sort >= 0");
                    table.CheckConstraint("ck_lessons_status", "status IN ('draft', 'published')");
                    table.ForeignKey(
                        name: "fk_lessons_course",
                        columns: x => new { x.tenant_id, x.course_id },
                        principalTable: "courses",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lesson_translations",
                columns: table => new
                {
                    lesson_id = table.Column<Guid>(type: "uuid", nullable: false),
                    locale = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    body = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lesson_translations", x => new { x.lesson_id, x.locale });
                    table.UniqueConstraint("ux_lesson_translations_tenant_id_locale_slug", x => new { x.tenant_id, x.locale, x.slug });
                    table.CheckConstraint("ck_lesson_translations_body_object", "jsonb_typeof(body) = 'object'");
                    table.CheckConstraint("ck_lesson_translations_slug_format", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$' AND slug !~ '^[0-9a-f]{32}$' AND slug !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'");
                    table.ForeignKey(
                        name: "fk_lesson_translations_lesson",
                        columns: x => new { x.tenant_id, x.lesson_id },
                        principalTable: "lessons",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_course_translations_tenant_id_course_id",
                table: "course_translations",
                columns: new[] { "tenant_id", "course_id" });

            migrationBuilder.CreateIndex(
                name: "ix_course_translations_tenant_id_organization_id",
                table: "course_translations",
                columns: new[] { "tenant_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_courses_tenant_id_organization_id",
                table: "courses",
                columns: new[] { "tenant_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_courses_tenant_id_organization_id_created_at_id",
                table: "courses",
                columns: new[] { "tenant_id", "organization_id", "created_at", "id" },
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_courses_tenant_id_slug_key",
                table: "courses",
                columns: new[] { "tenant_id", "slug_key" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lesson_translations_tenant_id_lesson_id",
                table: "lesson_translations",
                columns: new[] { "tenant_id", "lesson_id" });

            migrationBuilder.CreateIndex(
                name: "ix_lesson_translations_tenant_id_organization_id",
                table: "lesson_translations",
                columns: new[] { "tenant_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_lessons_tenant_id_course_id",
                table: "lessons",
                columns: new[] { "tenant_id", "course_id" });

            migrationBuilder.CreateIndex(
                name: "ix_lessons_tenant_id_course_id_sort_id",
                table: "lessons",
                columns: new[] { "tenant_id", "course_id", "sort", "id" },
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lessons_tenant_id_organization_id",
                table: "lessons",
                columns: new[] { "tenant_id", "organization_id" });

            // All four tables are tenant-owned and organization-scoped. Tenancy must
            // already provide its no-surrogate-id organization immutability function.
            migrationBuilder.Sql("""
                ALTER TABLE courses ENABLE ROW LEVEL SECURITY;
                ALTER TABLE courses FORCE ROW LEVEL SECURITY;

                CREATE POLICY courses_isolation ON courses
                    USING (
                        tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        AND (
                            organization_id IS NULL
                            OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                            OR current_setting('app.scope', true) = 'tenant'
                        )
                    )
                    WITH CHECK (
                        tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        AND (
                            (organization_id IS NULL
                             AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                            OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                        )
                    );
                CREATE POLICY courses_org_write_guard ON courses
                    AS RESTRICTIVE FOR UPDATE
                    USING (
                        (organization_id IS NULL
                         AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    );
                CREATE POLICY courses_org_delete_guard ON courses
                    AS RESTRICTIVE FOR DELETE
                    USING (
                        (organization_id IS NULL
                         AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    );
                CREATE TRIGGER tg_courses_organization_id_immutable
                    BEFORE UPDATE ON public.courses
                    FOR EACH ROW EXECUTE FUNCTION public.fn_organization_id_immutable();

                GRANT SELECT, INSERT, UPDATE ON courses TO learnstack_app;
                GRANT SELECT ON courses TO learnstack_platform;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE lessons ENABLE ROW LEVEL SECURITY;
                ALTER TABLE lessons FORCE ROW LEVEL SECURITY;

                CREATE POLICY lessons_isolation ON lessons
                    USING (
                        tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        AND (
                            organization_id IS NULL
                            OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                            OR current_setting('app.scope', true) = 'tenant'
                        )
                    )
                    WITH CHECK (
                        tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        AND (
                            (organization_id IS NULL
                             AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                            OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                        )
                    );
                CREATE POLICY lessons_org_write_guard ON lessons
                    AS RESTRICTIVE FOR UPDATE
                    USING (
                        (organization_id IS NULL
                         AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    );
                CREATE POLICY lessons_org_delete_guard ON lessons
                    AS RESTRICTIVE FOR DELETE
                    USING (
                        (organization_id IS NULL
                         AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    );
                CREATE TRIGGER tg_lessons_organization_id_immutable
                    BEFORE UPDATE ON public.lessons
                    FOR EACH ROW EXECUTE FUNCTION public.fn_organization_id_immutable();

                GRANT SELECT, INSERT, UPDATE ON lessons TO learnstack_app;
                GRANT SELECT ON lessons TO learnstack_platform;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE course_translations ENABLE ROW LEVEL SECURITY;
                ALTER TABLE course_translations FORCE ROW LEVEL SECURITY;

                CREATE POLICY course_translations_isolation ON course_translations
                    USING (
                        tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        AND (
                            organization_id IS NULL
                            OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                            OR current_setting('app.scope', true) = 'tenant'
                        )
                    )
                    WITH CHECK (
                        tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        AND (
                            (organization_id IS NULL
                             AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                            OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                        )
                    );
                CREATE POLICY course_translations_org_write_guard ON course_translations
                    AS RESTRICTIVE FOR UPDATE
                    USING (
                        (organization_id IS NULL
                         AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    );
                CREATE POLICY course_translations_org_delete_guard ON course_translations
                    AS RESTRICTIVE FOR DELETE
                    USING (
                        (organization_id IS NULL
                         AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    );
                CREATE TRIGGER tg_course_translations_organization_id_immutable
                    BEFORE UPDATE ON public.course_translations
                    FOR EACH ROW EXECUTE FUNCTION public.fn_organization_id_immutable();

                GRANT SELECT, INSERT ON course_translations TO learnstack_app;
                GRANT SELECT ON course_translations TO learnstack_platform;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE lesson_translations ENABLE ROW LEVEL SECURITY;
                ALTER TABLE lesson_translations FORCE ROW LEVEL SECURITY;

                CREATE POLICY lesson_translations_isolation ON lesson_translations
                    USING (
                        tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        AND (
                            organization_id IS NULL
                            OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                            OR current_setting('app.scope', true) = 'tenant'
                        )
                    )
                    WITH CHECK (
                        tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                        AND (
                            (organization_id IS NULL
                             AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                            OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                        )
                    );
                CREATE POLICY lesson_translations_org_write_guard ON lesson_translations
                    AS RESTRICTIVE FOR UPDATE
                    USING (
                        (organization_id IS NULL
                         AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    );
                CREATE POLICY lesson_translations_org_delete_guard ON lesson_translations
                    AS RESTRICTIVE FOR DELETE
                    USING (
                        (organization_id IS NULL
                         AND NULLIF(current_setting('app.organization_id', true), '') IS NULL)
                        OR organization_id = NULLIF(current_setting('app.organization_id', true), '')::uuid
                    );
                CREATE TRIGGER tg_lesson_translations_organization_id_immutable
                    BEFORE UPDATE ON public.lesson_translations
                    FOR EACH ROW EXECUTE FUNCTION public.fn_organization_id_immutable();

                GRANT SELECT, INSERT ON lesson_translations TO learnstack_app;
                GRANT SELECT ON lesson_translations TO learnstack_platform;
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION public.fn_lessons_parent_scope() RETURNS trigger
                    LANGUAGE plpgsql
                    SECURITY INVOKER
                    SET search_path = pg_catalog
                AS $$
                DECLARE
                    parent_organization_id uuid;
                BEGIN
                    SELECT parent.organization_id INTO parent_organization_id
                    FROM public.courses AS parent
                    WHERE parent.tenant_id = NEW.tenant_id AND parent.id = NEW.course_id
                    FOR KEY SHARE;

                    IF NOT FOUND OR parent_organization_id IS DISTINCT FROM NEW.organization_id THEN
                        RAISE EXCEPTION 'parent scope is unavailable or mismatched (table %)', TG_TABLE_NAME
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER tg_lessons_parent_scope
                    BEFORE INSERT OR UPDATE ON public.lessons
                    FOR EACH ROW EXECUTE FUNCTION public.fn_lessons_parent_scope();
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION public.fn_course_translations_parent_scope() RETURNS trigger
                    LANGUAGE plpgsql
                    SECURITY INVOKER
                    SET search_path = pg_catalog
                AS $$
                DECLARE
                    parent_organization_id uuid;
                BEGIN
                    SELECT parent.organization_id INTO parent_organization_id
                    FROM public.courses AS parent
                    WHERE parent.tenant_id = NEW.tenant_id AND parent.id = NEW.course_id
                    FOR KEY SHARE;

                    IF NOT FOUND OR parent_organization_id IS DISTINCT FROM NEW.organization_id THEN
                        RAISE EXCEPTION 'parent scope is unavailable or mismatched (table %)', TG_TABLE_NAME
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER tg_course_translations_parent_scope
                    BEFORE INSERT OR UPDATE ON public.course_translations
                    FOR EACH ROW EXECUTE FUNCTION public.fn_course_translations_parent_scope();
                """);

            migrationBuilder.Sql("""
                CREATE FUNCTION public.fn_lesson_translations_parent_scope() RETURNS trigger
                    LANGUAGE plpgsql
                    SECURITY INVOKER
                    SET search_path = pg_catalog
                AS $$
                DECLARE
                    parent_organization_id uuid;
                BEGIN
                    SELECT parent.organization_id INTO parent_organization_id
                    FROM public.lessons AS parent
                    WHERE parent.tenant_id = NEW.tenant_id AND parent.id = NEW.lesson_id
                    FOR KEY SHARE;

                    IF NOT FOUND OR parent_organization_id IS DISTINCT FROM NEW.organization_id THEN
                        RAISE EXCEPTION 'parent scope is unavailable or mismatched (table %)', TG_TABLE_NAME
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER tg_lesson_translations_parent_scope
                    BEFORE INSERT OR UPDATE ON public.lesson_translations
                    FOR EACH ROW EXECUTE FUNCTION public.fn_lesson_translations_parent_scope();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "course_translations");

            migrationBuilder.DropTable(
                name: "lesson_translations");

            migrationBuilder.DropTable(
                name: "lessons");

            migrationBuilder.DropTable(
                name: "courses");

            // Table removal drops their triggers first. The shared immutability
            // function belongs to Tenancy and survives this chain's reversal.
            migrationBuilder.Sql("""
                DROP FUNCTION public.fn_lesson_translations_parent_scope();
                DROP FUNCTION public.fn_course_translations_parent_scope();
                DROP FUNCTION public.fn_lessons_parent_scope();
                """);
        }
    }
}
