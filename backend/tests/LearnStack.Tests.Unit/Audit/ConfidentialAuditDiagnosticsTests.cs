using LearnStack.SharedKernel.Audit;
using Xunit;

namespace LearnStack.Tests.Unit.Audit;

public sealed class ConfidentialAuditDiagnosticsTests
{
    [Fact]
    public async Task Nested_confidential_act_protects_outer_completion_without_affecting_an_independent_capture()
    {
        var owner = new object();
        Assert.False(ConfidentialAuditDiagnostics.IsActive);
        using (ConfidentialAuditDiagnostics.BeginExecution(owner, confidential: false))
        {
            Assert.False(ConfidentialAuditDiagnostics.IsActive);
            await NestedAsync(owner);
            Assert.True(ConfidentialAuditDiagnostics.IsActive);
            using (ConfidentialAuditDiagnostics.BeginExecution(new object(), confidential: false))
                Assert.False(ConfidentialAuditDiagnostics.IsActive);
            Assert.True(ConfidentialAuditDiagnostics.IsActive);
        }
        Assert.False(ConfidentialAuditDiagnostics.IsActive);
        using (ConfidentialAuditDiagnostics.BeginExecution(owner, confidential: false))
            Assert.False(ConfidentialAuditDiagnostics.IsActive);
    }

    private static async Task NestedAsync(object owner)
    {
        using var scope = ConfidentialAuditDiagnostics.BeginExecution(owner, confidential: true);
        await Task.Yield();
        Assert.True(ConfidentialAuditDiagnostics.IsActive);
    }
}
