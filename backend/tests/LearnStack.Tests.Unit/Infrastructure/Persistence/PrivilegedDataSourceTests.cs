using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Tests.Unit.Application.Jobs;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Persistence;

public sealed class PrivilegedDataSourceTests
{
    [Fact]
    public async Task A_disposed_cold_facade_never_constructs_its_pool()
    {
        var source = new PrivilegedDataSource(() => throw new InvalidOperationException("must not build"));
        source.Dispose();
        var sync = () => source.OpenConnection();
        var asyncOpen = async () => await source.OpenConnectionAsync();
        sync.Should().Throw<ObjectDisposedException>();
        await asyncOpen.Should().ThrowAsync<ObjectDisposedException>();
        source.IsValueCreated.Should().BeFalse();
    }

    [Fact]
    public async Task Disposal_owns_a_pool_whose_factory_is_already_in_progress()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NpgsqlDataSource? built = null;
        var source = new PrivilegedDataSource(() =>
        {
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
            built = NpgsqlDataSource.Create("Host=localhost;Port=1;Database=learnstack");
            return built;
        });
        var opening = Task.Run(async () =>
        {
            try { await source.OpenConnectionAsync(new CancellationToken(canceled: true)); }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException) { }
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposing = Task.Run(() =>
        {
            disposalEntered.SetResult();
            source.Dispose();
        });
        await disposalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromMilliseconds(100)));
            disposing.IsCompleted.Should().BeFalse("disposal must join a factory already building its owned pool");
        }
        finally { release.SetResult(); }
        await Task.WhenAll(opening, disposing).WaitAsync(TimeSpan.FromSeconds(5));
        built.Should().NotBeNull();
        var rawOpen = () => built!.OpenConnection(); // Factory completion above guarantees this reference.
        rawOpen.Should().Throw<ObjectDisposedException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_admission_refuses_cold_and_warm_pool_sync_async_and_cancelled_access(bool warm)
    {
        var fixture = new BackgroundJobExecutionAdmissionTests.Fixture();
        var built = 0;
        await using var source = new PrivilegedDataSource(() =>
        {
            built++;
            return NpgsqlDataSource.Create("Host=localhost;Port=1;Username=learnstack_platform;Database=learnstack");
        });
        if (warm)
        {
            var open = async () => await source.OpenConnectionAsync(new CancellationToken(canceled: true));
            await open.Should().ThrowAsync<OperationCanceledException>();
        }
        using (fixture.Enter())
        {
            var sync = () => source.OpenConnection();
            sync.Should().Throw<InvalidOperationException>().WithMessage("*Ordinary background-job*");
            foreach (var token in new[] { CancellationToken.None, new CancellationToken(canceled: true) })
            {
                var asyncOpen = async () => await source.OpenConnectionAsync(token);
                await asyncOpen.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Ordinary background-job*");
            }
        }
        built.Should().Be(warm ? 1 : 0);
        source.IsValueCreated.Should().Be(warm);
    }

    [Fact]
    public async Task Cleanup_under_an_admission_does_not_build_a_cold_pool_or_refuse_a_warm_pool()
    {
        var fixture = new BackgroundJobExecutionAdmissionTests.Fixture();
        var cold = new PrivilegedDataSource(() => throw new InvalidOperationException("must stay lazy"));
        var warm = new PrivilegedDataSource(() => NpgsqlDataSource.Create("Host=localhost;Port=1;Database=learnstack"));
        var open = async () => await warm.OpenConnectionAsync(new CancellationToken(canceled: true));
        await open.Should().ThrowAsync<OperationCanceledException>();
        using (fixture.Enter())
        {
            cold.Dispose();
            await cold.DisposeAsync();
            warm.Dispose();
            await warm.DisposeAsync();
        }
        cold.IsValueCreated.Should().BeFalse();
    }

    [Fact]
    public void Facade_exposes_no_raw_source_or_lazy_credential_getter()
    {
        typeof(PrivilegedDataSource).GetProperties().Select(property => property.PropertyType)
            .Should().NotContain(typeof(NpgsqlDataSource)).And.NotContain(typeof(Lazy<NpgsqlDataSource>));
        typeof(PrivilegedDataSource).GetMethods().Select(method => method.ReturnType)
            .Should().NotContain(typeof(NpgsqlDataSource)).And.NotContain(typeof(Lazy<NpgsqlDataSource>));
        var guarded = () => BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        guarded.Should().NotThrow();
    }
}
