using System.Text;

namespace LearnStack.SharedKernel.Validation;

/// <summary>Transport-independent instance admission bound, checked before parsing.</summary>
/// <remarks>ADR-0043 caps entry instances at 1 MiB of UTF-8, independently of schema size.</remarks>
public static class JsonInstanceLimits
{
    public const int MaxBytes = 1024 * 1024;

    public static bool IsWithinCap(string value) =>
        value is not null && value.Length <= MaxBytes && Encoding.UTF8.GetByteCount(value) <= MaxBytes;
}
