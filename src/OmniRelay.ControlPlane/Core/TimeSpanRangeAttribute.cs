using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace OmniRelay.Core.Leadership;

/// <summary>Validates that a <see cref="TimeSpan"/> value falls within the configured bounds.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
internal sealed class TimeSpanRangeAttribute : ValidationAttribute
{
    public TimeSpanRangeAttribute(string minimum, string maximum)
    {
        Minimum = Parse(minimum, nameof(minimum));
        Maximum = Parse(maximum, nameof(maximum));
        if (Minimum > Maximum)
        {
            throw new ArgumentException("Minimum must be less than or equal to maximum.", nameof(minimum));
        }
    }

    public TimeSpan Minimum { get; }

    public TimeSpan Maximum { get; }

    public override bool IsValid(object? value)
    {
        if (value is null)
        {
            return true;
        }

        if (value is not TimeSpan timeSpan)
        {
            return false;
        }

        return timeSpan >= Minimum && timeSpan <= Maximum;
    }

    public override string FormatErrorMessage(string name) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0} must be between {1:c} and {2:c}.",
            name,
            Minimum,
            Maximum);

    private static TimeSpan Parse(string value, string paramName)
    {
        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var result))
        {
            throw new ArgumentException($"The value '{value}' is not a valid TimeSpan.", paramName);
        }

        return result;
    }
}
