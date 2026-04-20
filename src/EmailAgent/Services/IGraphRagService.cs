using EmailAgent.Models;

namespace EmailAgent.Services;

/// <summary>
/// Abstraction over the GraphRAG pipeline:
/// entity extraction, graph persistence, and context retrieval.
/// </summary>
public interface IGraphRagService
{
    Task<ExtractedEntities> ExtractEntitiesAsync(
        EmailItem email,
        CancellationToken cancellationToken = default);

    Task UpsertGraphAsync(
        EmailItem email,
        ExtractedEntities entities,
        CancellationToken cancellationToken = default);

    Task<GraphContext> GetGraphContextAsync(
        string senderAddress,
        CancellationToken cancellationToken = default);

    Task RecordResolutionAsync(
        string messageId,
        string replyBody,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Structured entities extracted from an email by the AI model.
/// </summary>
public sealed record ExtractedEntities(
    string SenderDomain,
    string? OrganizationName,
    IReadOnlyList<string> Topics,
    string IssueSummary,
    string PrimaryTopic);
