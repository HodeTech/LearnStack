using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Infrastructure.Persistence;

/// <summary>Reads only the current physical global announcement; no mutable context-setting port.</summary>
public sealed class GlobalIdentityScopeAccessor(IUnitOfWork unitOfWork) : IGlobalIdentityScopeAccessor
{
    public GlobalIdentityContextAnnouncement? Current => unitOfWork is IGlobalIdentityUnitOfWork global
        ? global.GetGlobalIdentityAnnouncement(unitOfWork.Transaction) : null;
}
