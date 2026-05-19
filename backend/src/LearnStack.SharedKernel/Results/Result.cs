namespace LearnStack.SharedKernel.Results;

public sealed record Result<T>(bool IsSuccess, T? Value, Error? Error)
{
    public static Result<T> Ok(T value) => new(true, value, null);

    public static Result<T> Fail(Error error) => new(false, default, error);
}
