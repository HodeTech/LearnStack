using FluentAssertions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed class GlobalIdentityModelTests
{
    [Fact]
    public void Historical_Identity_migrations_retain_their_ordered_SQL_operations()
    {
        // These already-applied migrations call shared DDL/manifest helpers.
        // Pin their emitted SQL independently of the latest database census.
        string[] historical = ["20261025201338_create_identity_schema", "20261025232309_add_contextual_identity",
            "20261026035353_correct_identity_parent_mirror_sqlstate", "20261026043921_correct_identity_admission_parent_sqlstate"];
        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in typeof(IdentityDbContext).Assembly.GetTypes())
        {
            var id = type.GetCustomAttribute<MigrationAttribute>()?.Id;
            if (id is null || !historical.Contains(id, StringComparer.Ordinal)) continue;
            var migration = (Migration)Activator.CreateInstance(type)!;
            migration.ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
            actual.Add(id + "/up", SqlDigest(migration.UpOperations));
            actual.Add(id + "/down", SqlDigest(migration.DownOperations));
        }
        actual.Should().BeEquivalentTo(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["20261025201338_create_identity_schema/up"] = "10934bfcd4e627f53453db73923089ef0091e7bb79ad3903148ad6dc8b77f2f9",
            ["20261025201338_create_identity_schema/down"] = "e73bf7c924bd93ff7c5a4b7b280b8662d1c8c92a68322fd08e000bee9b2ea553",
            ["20261025232309_add_contextual_identity/up"] = "b2e35b7a50a6bb282a7fe5ea898b075d6b6a1e104a422ac703bbd35b805fc4ea",
            ["20261025232309_add_contextual_identity/down"] = "3dcf51c56981f939d7e43ab39dca922ccc51bd51ce6a608f79327b5beac20a77",
            ["20261026035353_correct_identity_parent_mirror_sqlstate/up"] = "6d91942839a75e2570f7cc070fe253c8818477525c2af9f86ca51328fd68a39c",
            ["20261026035353_correct_identity_parent_mirror_sqlstate/down"] = "6e349e7ba68d3afd76d663d7f7cf4e5fa5a2beea23cf68eb73543a02c050e1d5",
            ["20261026043921_correct_identity_admission_parent_sqlstate/up"] = "60fbd523095628a9e2d10fb7385c711f25fb3a7ffb16c1761d93e69fecca8625",
            ["20261026043921_correct_identity_admission_parent_sqlstate/down"] = "9ab54f76e69a5638a7295a6cab63c3801c019aaa76cca1e51b29dcd38822edcb",
        });
    }

    private static string SqlDigest(IReadOnlyList<MigrationOperation> operations)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var sql in operations.OfType<SqlOperation>())
        {
            var bytes = Encoding.UTF8.GetBytes(sql.Sql);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    [Fact]
    public void Generated_snapshot_matches_the_current_Identity_model()
    {
        using var context = Modules.ModelOnly<IdentityDbContext>();
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public void User_Aggregate_Has_No_TenantScoped_Columns()
    {
        using var context = Modules.ModelOnly<IdentityDbContext>();
        context.Model.GetEntityTypes().Select(entity => entity.GetTableName())
            .Should().BeEquivalentTo(["users", "identity_authority", "memberships", "membership_profiles",
                "membership_role_assignments", "roles", "role_permission_grants"]);
        var user = context.Model.FindEntityType(typeof(User))!;
        user.GetProperties().Should().HaveCount(14);
        user.FindProperty("TenantId").Should().BeNull();
        user.FindProperty("OrganizationId").Should().BeNull();
        typeof(ITenantOwned).IsAssignableFrom(typeof(User)).Should().BeFalse();
        typeof(User).GetCustomAttributes(typeof(TenantOwnedAttribute), false).Should().BeEmpty();
        user.GetDeclaredQueryFilters().Should().ContainSingle();
        context.Model.FindEntityType(typeof(IdentityAuthority))!.GetDeclaredQueryFilters().Should().BeEmpty();
        Modules.Mapped.Count(module => module.Domain == typeof(User).Assembly).Should().Be(1);
        Modules.Scoped.Should().Contain(module => module.Domain == typeof(User).Assembly,
            "Identity's contextual roots and contained children require the same tenant/org sweep");
    }

    [Fact]
    public void User_binding_has_permanent_unique_key_creation_pair_and_sensitive_raw_subject()
    {
        using var context = Modules.ModelOnly<IdentityDbContext>();
        var user = context.Model.FindEntityType(typeof(User))!;
        var binding = user.GetIndexes().Single(index => index.GetDatabaseName() == "ux_users_authority_id_subject_key");
        binding.IsUnique.Should().BeTrue();
        binding.GetFilter().Should().BeNull();
        binding.Properties.Select(property => property.Name).Should().Equal("AuthorityId", "SubjectKey");
        user.FindProperty(nameof(User.CreationAuditId)).Should().NotBeNull();
        user.FindProperty(nameof(User.CreationAuditTimestamp)).Should().NotBeNull();
        user.GetForeignKeys().Should().ContainSingle(key => key.PrincipalEntityType.ClrType == typeof(IdentityAuthority));
        typeof(User).GetProperty(nameof(User.ProviderSubject))!
            .GetCustomAttributes(typeof(PiiSensitiveAttribute), true).Should().ContainSingle();
        foreach (var name in new[] { nameof(User.UpdatedAt), nameof(User.UpdatedBy), nameof(User.DeletedAt), nameof(User.DeletedBy) })
            user.FindProperty(name)!.GetBeforeSaveBehavior().Should().Be(PropertySaveBehavior.Ignore,
                "the creation-only INSERT grant excludes these four NULL columns");
    }
}
