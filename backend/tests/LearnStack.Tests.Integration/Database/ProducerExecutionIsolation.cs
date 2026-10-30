using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// API-host collections legitimately observe learnstack.* activities. This
/// collection runs after parallel collections finish so the real no-listener
/// producer fallback is measured without removing another host's observer.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProducerExecutionIsolation : ICollectionFixture<SchemaFixture>
{
    public const string Name = nameof(ProducerExecutionIsolation);
}
