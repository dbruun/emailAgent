namespace EmailAgent.Models;

/// <summary>
/// Structured context retrieved from the knowledge graph for a given sender.
/// </summary>
public sealed record GraphContext(
    string? OrganizationName,
    string? SupportTier,
    int PriorEmailCount,
    IReadOnlyList<PriorIssue> PriorIssues,
    IReadOnlyList<string> KnownTopics);

/// <summary>
/// A summary of a single past issue and its resolution (if available).
/// </summary>
public sealed record PriorIssue(
    string Subject,
    string Summary,
    string Status,
    DateTimeOffset CreatedAt,
    string? ResolutionSummary);
