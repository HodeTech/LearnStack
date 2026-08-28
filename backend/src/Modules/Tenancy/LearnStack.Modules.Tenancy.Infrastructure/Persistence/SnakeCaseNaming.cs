using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence;

/// <summary>
/// Rewrites every table, column, key, index and constraint name the model
/// produces into <c>snake_case</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not the `EFCore.NamingConventions` package.</b> Measured: the
/// only version compatible with EF Core 10 is <c>10.0.1</c>, and it requires
/// <c>Microsoft.EntityFrameworkCore &gt;= 10.0.1</c> while this repository pins
/// <c>10.0.0</c> in central package management — taking it means bumping the ORM
/// solution-wide, which is a larger change than a naming convention should make.
/// This is forty lines with no dependency and no version coupling.
/// </para>
/// <para>
/// <b>Why a convention and not `HasColumnName` per property.</b> Every RLS policy
/// predicate, every <c>GRANT</c> and every index name in
/// <see href="../../../../../../docs/standards/05-database.md">Database Standards</see>
/// is written against snake_case identifiers. Naming sixty columns by hand means a
/// forgotten one is silently <c>PascalCase</c> — a column the policy does not
/// mention and the grant does not cover. <c>Every_Mapped_Identifier_Is_Snake_Case</c>
/// is what makes the omission impossible rather than unlikely.
/// </para>
/// </remarks>
internal static class SnakeCaseNaming
{
    public static void ApplySnakeCaseNames(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            // An explicit ToTable in a configuration wins: the table name is set
            // before this runs, and converting an already-snake_case name is a
            // no-op, so there is no ordering hazard either way.
            var tableName = entity.GetTableName();
            if (tableName is not null)
            {
                entity.SetTableName(ToSnakeCase(tableName));
            }

            foreach (var property in entity.GetProperties())
            {
                var columnName = property.GetColumnName();
                if (columnName is not null)
                {
                    property.SetColumnName(ToSnakeCase(columnName));
                }
            }

            foreach (var key in entity.GetKeys())
            {
                var name = key.GetName();
                if (name is not null)
                {
                    key.SetName(ToSnakeCase(name));
                }
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                var name = foreignKey.GetConstraintName();
                if (name is not null)
                {
                    foreignKey.SetConstraintName(ToSnakeCase(name));
                }
            }

            foreach (var index in entity.GetIndexes())
            {
                var name = index.GetDatabaseName();
                if (name is not null)
                {
                    index.SetDatabaseName(ToSnakeCase(name));
                }
            }
        }
    }

    /// <summary>
    /// <c>TenantId</c> → <c>tenant_id</c>, <c>IsPubliclyLive</c> → <c>is_publicly_live</c>,
    /// <c>PK_Tenants</c> → <c>pk_tenants</c>.
    /// </summary>
    /// <remarks>
    /// A boundary is inserted before an upper-case letter that follows a
    /// lower-case letter or digit, and before the last upper-case letter of a run
    /// that is followed by a lower-case one — so <c>HTTPStatus</c> becomes
    /// <c>http_status</c> rather than <c>h_t_t_p_status</c>. An existing
    /// underscore is left alone, which is what makes the function idempotent and
    /// therefore safe to apply to a name a configuration already set.
    /// </remarks>
    internal static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 8);

        for (var index = 0; index < name.Length; index++)
        {
            var current = name[index];

            if (char.IsUpper(current) && index > 0)
            {
                var previous = name[index - 1];
                var startsNewWord =
                    !char.IsUpper(previous) && previous != '_'
                    || (index + 1 < name.Length && char.IsLower(name[index + 1]));

                if (startsNewWord && builder.Length > 0 && builder[^1] != '_')
                {
                    builder.Append('_');
                }
            }

            builder.Append(char.ToLower(current, CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
