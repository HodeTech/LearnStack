using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Branding;

/// <summary>Writes the existing complete theme contract in the current orgless tenant.</summary>
/// <param name="Theme">Complete closed palette, with the same validation as the Seed-only writer.</param>
/// <param name="ExpectedVersion">Null is create-only with a server-assigned setting id; a version is replace-only.</param>
public sealed record SetManagedTenantBrandingCommand([property: PiiSensitive] string Theme, long? ExpectedVersion)
    : IRequest<Result<long>>
{
    public override string ToString() => nameof(SetManagedTenantBrandingCommand);
}
