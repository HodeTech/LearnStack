using LearnStack.Infrastructure.Persistence;
using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Composition;

public sealed class OutboxDispatcherDataSourceGuardTests
{
    private const string Valid =
        "Host=localhost;Port=1;Database=learnstack;Username=learnstack_outbox_admin;Password=synthetic-secret-canary"; // leakwatch:ignore

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Host=localhost;Username=synthetic-username-canary;Password=synthetic-secret-canary")] // leakwatch:ignore
    [InlineData("Host=localhost;Username=LEARNSTACK_OUTBOX_ADMIN;Password=synthetic-secret-canary")] // leakwatch:ignore
    [InlineData("Host=localhost;Password=synthetic-secret-canary")] // leakwatch:ignore
    [InlineData("Host=localhost;Port=synthetic-secret-canary;Username=synthetic-username-canary")]
    [InlineData("postgres://synthetic-username-canary:synthetic-secret-canary@localhost/database")] // leakwatch:ignore
    [InlineData("synthetic-secret-canary=value;Username=synthetic-username-canary")]
    public void Validate_and_build_refuse_invalid_credentials_without_echo_or_inner_exception(string? value)
    {
        AssertSafeRefusal(() => OutboxDispatcherDataSource.Validate(value));
        AssertSafeRefusal(() => OutboxDispatcherDataSource.Build(value));
    }

    [Fact]
    public void A_valid_credential_validates_and_builds_without_connecting()
    {
        OutboxDispatcherDataSource.Validate(Valid);
        using var source = OutboxDispatcherDataSource.Build(Valid);
        source.Should().NotBeNull();
    }

    [Theory]
    [InlineData("Host=localhost;Username=synthetic-username-canary;Password=synthetic-secret-canary")] // leakwatch:ignore
    [InlineData("postgres://synthetic-username-canary:synthetic-secret-canary@localhost/database")] // leakwatch:ignore
    [InlineData("Host=localhost;Port=synthetic-secret-canary;Username=learnstack_outbox_admin")]
    public void Present_invalid_configuration_is_refused_during_registration(string value)
    {
        var services = new ServiceCollection();
        AssertSafeRefusal(() => services.AddLearnStackOutboxDispatcher(Configuration(value)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Absent_configuration_is_deferred_until_the_keyed_source_is_resolved(string? value)
    {
        var services = new ServiceCollection();
        services.AddLearnStackOutboxDispatcher(Configuration(value));
        using var provider = services.BuildServiceProvider();
        var lazy = provider.GetRequiredKeyedService<PrivilegedDataSource>(OutboxDispatcherDataSource.Key);

        lazy.IsValueCreated.Should().BeFalse();
        AssertSafeRefusal(() => lazy.OpenConnection());
    }

    [Fact]
    public async Task Registration_is_keyed_lazy_and_owns_one_shared_source()
    {
        var services = new ServiceCollection();
        services.AddLearnStackOutboxDispatcher(Configuration(Valid));
        services.AddLearnStackOutboxDispatcher(Configuration(Valid));
        using var provider = services.BuildServiceProvider();
        var lazy = provider.GetRequiredKeyedService<PrivilegedDataSource>(OutboxDispatcherDataSource.Key);

        provider.GetService<NpgsqlDataSource>().Should().BeNull();
        lazy.IsValueCreated.Should().BeFalse();
        var open = async () => await lazy.OpenConnectionAsync(new CancellationToken(canceled: true));
        await open.Should().ThrowAsync<OperationCanceledException>();
        lazy.IsValueCreated.Should().BeTrue();
        lazy.Should().BeSameAs(provider.GetRequiredKeyedService<PrivilegedDataSource>(OutboxDispatcherDataSource.Key));
        provider.GetKeyedService<NpgsqlDataSource>(OutboxDispatcherDataSource.Key).Should().BeNull();
        provider.GetKeyedService<Lazy<NpgsqlDataSource>>(OutboxDispatcherDataSource.Key).Should().BeNull();
        services.Count(descriptor => descriptor.IsKeyedService
            && descriptor.ServiceType == typeof(PrivilegedDataSource)
            && Equals(descriptor.ServiceKey, OutboxDispatcherDataSource.Key)).Should().Be(1);
    }

    private static IConfiguration Configuration(string? value) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{OutboxDispatcherDataSource.ConnectionName}"] = value,
        })
        .Build();

    private static void AssertSafeRefusal(Action action)
    {
        var failure = action.Should().Throw<InvalidOperationException>().Which;
        failure.Message.Should().Contain("ConnectionStrings:OutboxDispatcher");
        failure.InnerException.Should().BeNull();
        failure.ToString().Should().NotContain("synthetic-secret-canary").And.NotContain("synthetic-username-canary");
    }
}
