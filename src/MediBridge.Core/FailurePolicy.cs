namespace MediBridge.Core;

public sealed record FailureDescriptor(string Kind, string Detail, int? HttpStatus = null, int? SqlError = null);

public static class FailureClassifier
{
    public static FailureCategory Classify(FailureDescriptor failure) => failure switch
    {
        { Kind: "BusinessRule" } => FailureCategory.BusinessRule,
        { Kind: "Timeout" } => FailureCategory.Transient,
        { HttpStatus: 408 } => FailureCategory.Transient,
        { HttpStatus: >= 500 and <= 599 } => FailureCategory.Transient,
        { SqlError: -2 or 1205 or 40197 or 40501 or 40613 or 10928 or 10929 or 49918 or 49919 or 49920 } => FailureCategory.Transient,
        _ => FailureCategory.Unclassified // conservative manual review; unknown failures are not retried
    };
}

public static class RetryPolicy
{
    public const int MaxAttempts = 5;
    public static TimeSpan? Delay(int completedAttempts, double jitter)
    {
        if (completedAttempts is < 1 or >= MaxAttempts) return null;
        if (jitter is < 0 or > 1 || double.IsNaN(jitter)) throw new ArgumentOutOfRangeException(nameof(jitter));
        return TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, completedAttempts - 1) * (0.5 + jitter)));
    }
}
