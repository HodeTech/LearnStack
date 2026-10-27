namespace LearnStack.SharedKernel.Permissions;

/// <summary>Read-only access to the actual current act; it cannot mint or replace authority.</summary>
public interface IOperationInvocationContext
{
    OperationInvocation? CurrentInvocation { get; }
    ProtectedOperationDecision? CurrentDecision { get; }
}
