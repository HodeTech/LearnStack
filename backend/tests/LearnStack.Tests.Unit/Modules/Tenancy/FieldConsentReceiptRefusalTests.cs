using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Tenancy.Application.FieldConsents;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Modules.Tenancy;

public sealed class FieldConsentReceiptRefusalTests
{
    private static readonly Guid Evidence = Guid.Parse("018f4d40-0000-7000-8000-000000000011");

    [Fact]
    public async Task Grant_receipt_without_current_invocation_never_reads_the_evidence()
    {
        var reader = Substitute.For<IOwnFieldConsentReceiptReader>();
        var invocations = Substitute.For<IOperationInvocationContext>();
        var handler = new ReadOwnGrantedFieldConsentReceiptQueryHandler(reader, Substitute.For<IOperationPolicyCatalog>(),
            invocations, Substitute.For<IProtectedOperationAuthorizer>(), Substitute.For<IAuthorizationRefusalReporter>(),
            Substitute.For<ITenantContext>(), Substitute.For<IAuditSubject>());
        var result = await handler.Handle(new ReadOwnGrantedFieldConsentReceiptQuery(Evidence), CancellationToken.None);
        Assert.True(result.IsFailure);
        Assert.Empty(reader.ReceivedCalls());
    }

    [Fact]
    public async Task Withdrawal_receipt_without_original_subject_witness_never_reads_the_evidence()
    {
        var reader = Substitute.For<IOwnFieldConsentReceiptReader>();
        var authorization = new OwnConsentEntryAuthorization(Substitute.For<IOperationInvocationContext>(),
            Substitute.For<IOperationPolicyCatalog>(), Substitute.For<IUnitOfWork>(), Substitute.For<ITenantContext>());
        var handler = new ReadOwnWithdrawnFieldConsentReceiptQueryHandler(reader, authorization, Substitute.For<IAuditSubject>());
        var result = await handler.Handle(new ReadOwnWithdrawnFieldConsentReceiptQuery(Evidence), CancellationToken.None);
        Assert.True(result.IsFailure);
        Assert.Empty(reader.ReceivedCalls());
    }
}
