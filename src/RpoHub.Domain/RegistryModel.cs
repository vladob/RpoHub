namespace RpoHub.Domain;

public enum IdentifierKind { Ico, TaxId, VatId, BirthNumber, SourceEntityId, Other }
public enum ObservationSeverity { Information, Warning, Error }

public sealed record SubjectIdentifier(
    IdentifierKind Kind,
    string Value,
    string SourceCode,
    DateOnly? ValidFrom = null,
    DateOnly? ValidTo = null,
    bool IsVerified = false);

public sealed record SourceRecordKey(string SourceCode, string SourceEntityId);

public sealed record DataQualityObservation(
    string RuleCode,
    ObservationSeverity Severity,
    string Message,
    SourceRecordKey SourceRecord,
    DateTimeOffset ObservedAtUtc);

public sealed class Subject
{
    private readonly List<SubjectIdentifier> _identifiers = [];
    public Guid Id { get; init; } = Guid.NewGuid();
    public string? DisplayName { get; private set; }
    public IReadOnlyCollection<SubjectIdentifier> Identifiers => _identifiers;

    public void SetDisplayName(string? value) => DisplayName = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void AddIdentifier(SubjectIdentifier identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier.Value);
        if (!_identifiers.Contains(identifier)) _identifiers.Add(identifier);
    }
}
