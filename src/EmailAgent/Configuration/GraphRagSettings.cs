namespace EmailAgent.Configuration;

/// <summary>
/// Configuration for the GraphRAG feature backed by Azure Cosmos DB for Apache Gremlin.
/// Bind from appsettings.json under the "GraphRag" key.
/// </summary>
public sealed class GraphRagSettings
{
    /// <summary>
    /// Gremlin WebSocket endpoint.
    /// Format: wss://&lt;account&gt;.gremlin.cosmos.azure.com:443/
    /// </summary>
    public string GremlinEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// Cosmos DB database name that contains the Gremlin graph.
    /// </summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// Name of the Gremlin graph container within the database.
    /// </summary>
    public string GraphName { get; set; } = string.Empty;

    /// <summary>
    /// Cosmos DB account key (primary or secondary).
    /// Use Azure Key Vault or user secrets; never commit to source control.
    /// </summary>
    public string AccountKey { get; set; } = string.Empty;

    /// <summary>
    /// Partition key value used for all vertices in a single-partition setup.
    /// Switch to per-entity partitioning for large-scale deployments.
    /// </summary>
    public string PartitionKey { get; set; } = "email-agent";

    /// <summary>
    /// Maximum number of prior issues to include in the graph context.
    /// Higher values provide richer context but increase prompt token usage.
    /// </summary>
    public int MaxRelatedIssues { get; set; } = 3;

    /// <summary>
    /// When false, GraphRAG entity extraction and graph operations are skipped.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
