using System.Text.Json.Serialization;

namespace JackiVpn.Domain.Common;

[JsonConverter(typeof(JsonStringEnumConverter<IssueSeverity>))]
public enum IssueSeverity
{
    Info = 0,
    Warning,
    Error,
}

public sealed record ValidationIssue(IssueSeverity Severity, string Field, string Message)
{
    public override string ToString() => $"{Severity}: {Field}: {Message}";
}

public sealed class ValidationResult
{
    private readonly List<ValidationIssue> _issues = [];

    public IReadOnlyList<ValidationIssue> Issues => _issues;

    public bool IsValid => !_issues.Any(i => i.Severity == IssueSeverity.Error);

    public IEnumerable<ValidationIssue> Errors => _issues.Where(i => i.Severity == IssueSeverity.Error);

    public IEnumerable<ValidationIssue> Warnings => _issues.Where(i => i.Severity == IssueSeverity.Warning);

    public ValidationResult Error(string field, string message)
    {
        _issues.Add(new ValidationIssue(IssueSeverity.Error, field, message));
        return this;
    }

    public ValidationResult Warn(string field, string message)
    {
        _issues.Add(new ValidationIssue(IssueSeverity.Warning, field, message));
        return this;
    }

    public ValidationResult Info(string field, string message)
    {
        _issues.Add(new ValidationIssue(IssueSeverity.Info, field, message));
        return this;
    }

    public void Merge(ValidationResult other)
    {
        ArgumentNullException.ThrowIfNull(other);
        _issues.AddRange(other._issues);
    }

    public override string ToString() => string.Join("; ", _issues);
}
