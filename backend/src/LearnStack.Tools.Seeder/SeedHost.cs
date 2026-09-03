using Microsoft.Extensions.Logging;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Tools.Seeder;

/// <summary>Source-generated logging, per the house CA1848 rule.</summary>
public static partial class SeedLog
{
    [LoggerMessage(EventId = 7001, Level = LogLevel.Error, Message = "Seeding failed.")]
    public static partial void Failed(ILogger logger, Exception exception);
}

/// <summary>The accessor the runner writes between acts.</summary>
public sealed class SeedTenantContextAccessor : ITenantContextAccessor
{
    public ITenantContext? Current { get; set; }
}
