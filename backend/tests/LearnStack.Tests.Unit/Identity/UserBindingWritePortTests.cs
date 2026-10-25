using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

public sealed class UserBindingWritePortTests
{
    [Fact]
    public async Task The_countable_global_write_port_refuses_updates_before_accessing_persistence()
    {
        await using var source = NpgsqlDataSource.Create(
            "Host=127.0.0.1;Port=1;Database=unopened;Username=learnstack_app;Timeout=1");
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var context = new IdentityDbContext(
            new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(source).Options,
            StaticTenantContextAccessor.Unresolved, new GlobalIdentityScopeAccessor(unit));
        IUserBindingStore binding = new UserBindingStore(context, unit);
        IAggregateWriteStore<User, UserId> root = binding;

        // Unadmitted update ownership refuses before inspecting any personal payload.
        var update = () => root.UpdateAsync(null!);
        (await update.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be("Global identity binding updates are not admitted.");
        unit.HasActiveTransaction.Should().BeFalse();
        unit.Mode.Should().BeNull();
    }
}
