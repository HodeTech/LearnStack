using System.Reflection;
using LearnStack.SharedKernel.Localization;

namespace LearnStack.SharedKernel.Results;

/// <summary>
/// Static helpers for constructing <see cref="Result{T}"/> instances when
/// the concrete generic parameter is known only at runtime (e.g. inside
/// the MediatR <c>ValidationBehavior</c> / <c>AuthorizationBehavior</c>
/// that short-circuit a handler without referencing the value type).
/// Per ADR-0032 § Sub-decision 3.
/// </summary>
public static class Result
{
    public static Result<T> Ok<T>(T value, LocalizedMessage? message = null) =>
        Result<T>.Ok(value, message);

    public static Result<T> Fail<T>(Error error) => Result<T>.Fail(error);

    /// <summary>
    /// Reflection-friendly failure factory used by MediatR pipeline
    /// behaviors whose <c>TResponse</c> is itself a <c>Result&lt;TValue&gt;</c>.
    /// Returns an instance of <typeparamref name="TResponse"/> — not
    /// <c>Result&lt;TResponse&gt;</c> — by reflecting over the closed
    /// generic to invoke the correct <c>Result&lt;TValue&gt;.Fail(error)</c>.
    /// </summary>
    /// <remarks>
    /// The reflected <see cref="MethodInfo"/> is cached per closed
    /// <typeparamref name="TResponse"/> in the nested
    /// <see cref="FailForCache{TResponse}"/> — initialised once on first
    /// touch, zero per-call overhead afterwards. The hot path is the
    /// MediatR pipeline behavior that runs on every command.
    /// </remarks>
    public static TResponse FailFor<TResponse>(Error error)
        where TResponse : IResultBase
    {
        ArgumentNullException.ThrowIfNull(error);

        var fail = FailForCache<TResponse>.FailMethod
            ?? throw new InvalidOperationException(
                $"Result.FailFor<TResponse> requires TResponse to be a closed Result<T>; got {typeof(TResponse).FullName}.");

        return (TResponse)fail.Invoke(obj: null, parameters: [error])!;
    }

    private static class FailForCache<TResponse>
        where TResponse : IResultBase
    {
        public static readonly MethodInfo? FailMethod = ResolveFailMethod();

        private static MethodInfo? ResolveFailMethod()
        {
            var responseType = typeof(TResponse);
            if (!responseType.IsGenericType
                || responseType.GetGenericTypeDefinition() != typeof(Result<>))
            {
                return null;
            }

            return responseType.GetMethod(
                nameof(Result<int>.Fail),
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: [typeof(Error)],
                modifiers: null);
        }
    }
}
