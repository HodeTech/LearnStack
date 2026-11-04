using LearnStack.Infrastructure.Email;
using LearnStack.SharedKernel.Email;

namespace LearnStack.Api.Composition;

/// <summary>Operator secrets and transport configuration are fixed and validated before workers start.</summary>
internal sealed class InvitationEmailStartupService(InvitationEmailCipher cipher, IEmailProvider provider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Construction validates the complete key ring and SMTP configuration without opening SMTP.
        ArgumentNullException.ThrowIfNull(cipher);
        ArgumentNullException.ThrowIfNull(provider);
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
