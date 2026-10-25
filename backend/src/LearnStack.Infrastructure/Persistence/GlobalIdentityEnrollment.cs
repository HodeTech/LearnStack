using LearnStack.SharedKernel.Identity;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>The scoped, non-allocating reader shared by the actual unit and the central audit store.</summary>
public sealed class GlobalIdentityEnrollment : IGlobalIdentityEnrollment
{
    private NpgsqlUnitOfWork? _unit;

    internal void Attach(NpgsqlUnitOfWork unit)
    {
        if (_unit is not null && !ReferenceEquals(_unit, unit))
            throw new InvalidOperationException("A scoped identity enrollment belongs to exactly one unit of work.");
        _unit = unit;
    }

    public bool IsActive => _unit?.IsGlobalIdentityTransaction is true;
}
