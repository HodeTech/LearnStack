using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

public sealed class UserBindingStore(IdentityDbContext context, IGlobalIdentityUnitOfWork unit) : IUserBindingStore
{
    public Task<User?> FindExactAsync(GlobalIdentitySubject subject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        EnrollCurrentOwner();
        return context.Users.AsNoTracking().SingleOrDefaultAsync(user =>
            user.AuthorityId == subject.AuthorityId && user.SubjectKey == subject.SubjectKey, cancellationToken);
    }

    public async Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        try
        {
            context.Users.Add(user);
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new UserBindingStorageException();
        }
    }

    private void EnrollCurrentOwner()
    {
        var announcement = unit.GetGlobalIdentityAnnouncement(unit.Transaction)
            ?? throw new InvalidOperationException("The binding store requires its current global identity owner.");
        unit.EnrollGlobalIdentityContext(context, announcement.Descriptor);
        context.Database.UseTransaction(unit.Transaction);
    }
}
