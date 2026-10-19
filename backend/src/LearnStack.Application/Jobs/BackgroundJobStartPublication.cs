namespace LearnStack.Application.Jobs;

/// <summary>Acknowledgement publication is independent of admission's actual scope cleanup.</summary>
public sealed class BackgroundJobStartPublication
{
    private readonly TaskCompletionSource<ConfirmedStartReceipt?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AdmitBackgroundJobAttemptCommand? _command;
    private int _bound;

    public Task<ConfirmedStartReceipt?> Completion => _completion.Task;

    /// <summary>The sole executor binds the actual command before sending it.</summary>
    public void Bind(AdmitBackgroundJobAttemptCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Interlocked.Exchange(ref _bound, 1) != 0 || _completion.Task.IsCompleted)
            throw new InvalidOperationException("Admission publication binds one actual command.");
        _command = command;
        command.BindPublication(this);
    }

    internal void Publish(AdmitBackgroundJobAttemptCommand command, ConfirmedStartReceipt receipt)
    {
        if (!ReferenceEquals(_command, command) || !ReferenceEquals(command.Receipt, receipt)
            || !_completion.TrySetResult(receipt))
            throw new InvalidOperationException("Only this command's acknowledged receipt may be published.");
    }

    /// <summary>Actual executor completion settles a start that produced no acknowledged receipt.</summary>
    public void SettleNoReceipt()
    {
        if (_command?.Receipt is null) _completion.TrySetResult(null);
    }
}
