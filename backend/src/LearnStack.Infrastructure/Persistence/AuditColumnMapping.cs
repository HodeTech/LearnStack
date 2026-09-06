using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>
/// The mapping rules every module's configurations apply to the columns
/// <c>AuditableEntity&lt;TId&gt;</c> defines, and to a closed-set enum.
/// </summary>
/// <remarks>
/// Here rather than once per module for the reason
/// <see cref="SnakeCaseNaming"/> is here: two copies of a convention are two
/// conventions the day one of them is edited, and these two had already begun to
/// diverge in what they explained while their bodies stayed identical.
/// </remarks>
public static class AuditColumnMapping
{
    /// <summary>
    /// Maps the six audit columns and the concurrency token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The concurrency token takes exactly three calls, and
    /// <see href="../../../../docs/decisions/0039-optimistic-concurrency-token.md">ADR-0039
    /// Amendment 2</see> fixes them: <c>HasDefaultValue(0L)</c> for the DDL
    /// template's <c>DEFAULT 0</c>, <c>IsConcurrencyToken()</c> for the token
    /// itself, and <c>ValueGeneratedNever()</c> because the first call otherwise
    /// leaves <c>ValueGenerated</c> at <c>OnAdd</c> — which is what
    /// <c>Aggregates_With_Optimistic_Concurrency_Map_RowVersion</c> rejects.
    /// </para>
    /// <para>
    /// <c>ValueGeneratedOnAddOrUpdate()</c> — and the equivalent
    /// <c>IsRowVersion()</c> — are the two calls that may never appear. They tell
    /// EF the database generates the value, and EF then omits the column from the
    /// <c>UPDATE</c> entirely: measured, the persisted value stays <c>0</c> for the
    /// life of the row, every <c>If-Match</c> compares equal, and a lost update
    /// succeeds while reporting success (ADR-0039 Amendment 1).
    /// </para>
    /// <para>
    /// <c>updated_at</c> / <c>updated_by</c> are nullable because
    /// <c>MarkCreated</c> stamps neither: a row that has never been changed has no
    /// updater, and <c>NOT NULL</c> would reject every insert.
    /// <c>deleted_at</c> / <c>deleted_by</c> are unconditional because
    /// <c>AuditableEntity&lt;TId&gt;</c> implements <c>ISoftDelete</c> for every
    /// aggregate, so EF maps them whether the aggregate is ever soft-deleted or
    /// not.
    /// </para>
    /// </remarks>
    public static void MapAuditColumns<TEntity, TId>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : AuditableEntity<TId>
        where TId : struct, IStronglyTypedId<Guid>, IEquatable<TId>
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Property(x => x.CreatedAt).IsRequired();
        builder.Property(x => x.CreatedBy)
            .HasConversion<UserId.EfCoreValueConverter, UserId.EfCoreValueComparer>()
            .IsRequired();
        builder.Property(x => x.UpdatedAt);
        builder.Property(x => x.UpdatedBy)
            .HasConversion<UserId.EfCoreValueConverter, UserId.EfCoreValueComparer>();
        builder.Property(x => x.DeletedAt);
        builder.Property(x => x.DeletedBy)
            .HasConversion<UserId.EfCoreValueConverter, UserId.EfCoreValueComparer>();

        builder.Property(x => x.Version)
            .HasColumnName("row_version")
            .HasDefaultValue(0L)
            .IsConcurrencyToken()
            .ValueGeneratedNever();
    }

    /// <summary>
    /// Maps a closed-set enum as <c>text</c> with the CLR name as the stored value.
    /// </summary>
    /// <remarks>
    /// Not a PostgreSQL <c>enum</c> type, whose values can only be added and never
    /// removed or reordered, and not an <c>int</c>, which makes a dump unreadable
    /// and a mistyped value indistinguishable from a valid one. The migration adds
    /// the matching <c>CHECK</c>, which is what actually bounds the column —
    /// this only decides how it is written. The store type is <c>text</c>, not
    /// <c>varchar(n)</c>:
    /// <see href="../../../../docs/standards/05-database.md">Database Standards</see>
    /// § Column types fixes the canonical form for a closed set as
    /// "<c>text NOT NULL</c> with a <c>CHECK (col IN (…))</c>", and a length cap
    /// beside an enumerating CHECK is a second, weaker bound that can only
    /// disagree with the first.
    /// </remarks>
    public static PropertyBuilder<TEnum> HasEnumAsText<TEnum>(this PropertyBuilder<TEnum> builder)
        where TEnum : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .HasConversion(
                value => value.ToString(),
                text => Enum.Parse<TEnum>(text, ignoreCase: false))
            .HasColumnType("text");
    }
}
