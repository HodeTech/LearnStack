using FluentAssertions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed class GlobalIdentityModelTests
{
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
