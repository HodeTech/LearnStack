using LearnStack.Api.Authentication;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using Microsoft.AspNetCore.Http;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

public sealed class ConsentSubjectReplaySourceTests
{
    [Fact]
    public void Receipt_selection_without_live_captured_entry_never_creates_a_request()
    {
        var http = new DefaultHttpContext();
        using var source = new ConsentSubjectSource(new HttpContextAccessor { HttpContext = http }, null!, null!, null!);
        http.Features.Set(source);
        Assert.False(source.SelectWithdrawalReceipt(http, Guid.Parse("018f4d40-0000-7000-8000-000000000011")));
        Assert.Null(source.ReceiptRequest);
        http.Features.Set<ConsentSubjectSource>(null);
        Assert.False(source.SelectWithdrawalReceipt(http, Guid.Parse("018f4d40-0000-7000-8000-000000000011")));
        Assert.Null(source.ReceiptRequest);
        ((IDisposable)source).Dispose();
        Assert.False(source.IsCurrent(http));
        Assert.False(source.SelectWithdrawalReceipt(http, Guid.Parse("018f4d40-0000-7000-8000-000000000011")));
    }
    [Fact]
    public void Request_feature_retains_the_exact_selected_object_and_rejects_unrelated_objects()
    {
        var evidence = Guid.Parse("018f4d40-0000-7000-8000-000000000011");
        var command = new WithdrawOwnMembershipFieldConsentCommand(evidence, 1, evidence, true);
        var original = new ConsentSubjectRequest(command);
        var replacement = new ConsentSubjectRequest(command);
        Assert.Same(command, original.Request);
        Assert.NotSame(original, replacement);
        var receipt = new ReadOwnWithdrawnFieldConsentReceiptQuery(evidence);
        var selected = new ConsentSubjectRequest(receipt);
        Assert.Same(receipt, selected.Request);
        Assert.NotSame(original.Request, selected.Request);
        Assert.Throws<ArgumentException>(() => new ConsentSubjectRequest(new object()));
    }

    [Fact]
    public void Minter_lifetime_closes_even_when_disposed_through_its_base_type()
    {
        var probe = new LifetimeProbe();
        Assert.True(probe.Live);
        ((ConsentSubjectScopeWitness.MintingSource)probe).Dispose();
        Assert.False(probe.Live);
    }

    private sealed class LifetimeProbe : ConsentSubjectScopeWitness.MintingSource
    {
        internal bool Live => IsOpen;
        protected override bool IsEvidenceCurrent(object evidence, OperationInvocation invocation) => false;
    }
}
