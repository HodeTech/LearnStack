using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Locales;

/// <summary>Adds bounded locale membership under the current orgless tenant's root version.</summary>
public sealed record AddManagedTenantLocaleCommand(long ExpectedVersion, [property: PiiSensitive] string Locale,
    bool IsEnabled, bool IsDefault, short Sort) : IRequest<Result<long>>
{
    public override string ToString() => nameof(AddManagedTenantLocaleCommand);
}

public sealed record SetManagedDefaultTenantLocaleCommand(long ExpectedVersion, [property: PiiSensitive] string Locale)
    : IRequest<Result<long>>
{
    public override string ToString() => nameof(SetManagedDefaultTenantLocaleCommand);
}

public sealed record SetTenantLocaleEnabledCommand(long ExpectedVersion, [property: PiiSensitive] string Locale, bool IsEnabled)
    : IRequest<Result<long>>
{
    public override string ToString() => nameof(SetTenantLocaleEnabledCommand);
}

/// <summary>Removes only nondefault membership; translation content remains retained.</summary>
public sealed record RemoveTenantLocaleCommand(long ExpectedVersion, [property: PiiSensitive] string Locale)
    : IRequest<Result<long>>
{
    public override string ToString() => nameof(RemoveTenantLocaleCommand);
}
