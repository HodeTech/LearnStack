using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.CustomFields;
using LearnStack.Modules.Identity.Application.CustomFields;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

public sealed class MembershipCustomFieldInvocationTests
{
    [Fact]
    public async Task Direct_mutation_handlers_refuse_before_read_or_fence_without_the_exact_application_invocation()
    {
        var reader = Substitute.For<IMembershipAdministrationReader>();
        var store = Substitute.For<IMembershipCustomFieldStore>();
        var locks = Substitute.For<IIdentityMutationLocks>();
        var executor = new MembershipCustomFieldCommandExecutor(store, reader, Substitute.For<ITenantContext>(), locks,
            Substitute.For<IIdentityScopeLifecycleReader>(), Substitute.For<IOperationInvocationContext>(), UnreachableGate,
            Fields(), Substitute.For<IAuditSubject>(), Substitute.For<IClock>());
        var own = await new SetOwnMembershipCustomFieldsCommandHandler(executor).Handle(new(0, new([], [])), default);
        var admin = await new SetMembershipCustomFieldsCommandHandler(executor).Handle(new(Guid.CreateVersion7(), 0, new([], [])), default);
        Assert.Equal("forbidden", own.Error!.Code);
        Assert.Equal("forbidden", admin.Error!.Code);
        Assert.Empty(reader.ReceivedCalls());
        Assert.Empty(store.ReceivedCalls());
        Assert.Empty(locks.ReceivedCalls());
    }

    [Fact]
    public async Task Direct_disclosure_handlers_refuse_without_an_authorized_application_invocation()
    {
        var reader = Substitute.For<IMembershipAdministrationReader>();
        var executor = new MembershipCustomFieldQueryExecutor(reader, Substitute.For<IOperationInvocationContext>(), UnreachableGate,
            Fields(), Substitute.For<IAuditSubject>());
        var own = await new GetOwnMembershipCustomFieldsQueryHandler(executor).Handle(new(), default);
        var admin = await new GetMembershipCustomFieldsQueryHandler(executor).Handle(new(Guid.CreateVersion7()), default);
        Assert.Equal("forbidden", own.Error!.Code);
        Assert.Equal("forbidden", admin.Error!.Code);
        Assert.Empty(reader.ReceivedCalls());
    }

    private static MembershipAdministrationAuthorization UnreachableGate() => throw new InvalidOperationException("Authority factory must not run.");
    private static MembershipCustomFieldService Fields() => new(Substitute.For<IExactCustomFieldDefinitionReader>(),
        Substitute.For<ICustomFieldValueValidator>(), Substitute.For<IMembershipFieldConsentReader>());
}
