namespace EmailAgent.Configuration;

/// <summary>
/// Configuration settings for Azure AI Foundry (project endpoint, model deployment,
/// and connected Azure AI Search / SharePoint knowledge sources).
/// Bind this section from appsettings.json under the "AIFoundry" key.
/// </summary>
public sealed class AIFoundrySettings
{
    /// <summary>
    /// The Azure AI Foundry project endpoint URL.
    /// Format: https://&lt;region&gt;.api.azureml.ms/
    /// </summary>
    public string ProjectEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// The name of the model deployment to use for the agent (e.g. "gpt-4o").
    /// Corresponds to the "Name" column in the Foundry portal → Models + endpoints tab.
    /// </summary>
    public string ModelDeploymentName { get; set; } = string.Empty;

    /// <summary>
    /// The Foundry project connection ID for the Azure AI Search resource.
    /// Used to configure the AzureAISearch tool so the agent can query the search index.
    /// </summary>
    public string AISearchConnectionId { get; set; } = string.Empty;

    /// <summary>
    /// Name of the Azure AI Search index that contains documents from
    /// Azure Blob Storage (and/or SharePoint content crawled by the indexer).
    /// </summary>
    public string AISearchIndexName { get; set; } = string.Empty;

    /// <summary>
    /// The Foundry project connection ID for the SharePoint grounding tool.
    /// Leave empty to skip SharePoint grounding.
    /// </summary>
    public string SharePointConnectionId { get; set; } = string.Empty;

    /// <summary>
    /// Internal name used to register the agent in the Foundry Agent Administration.
    /// Defaults to "email-agent".
    /// </summary>
    public string AgentName { get; set; } = "email-agent";
}
