#pragma warning disable AAIP001  // Azure AI Projects types are in preview

using System.ClientModel;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.AI.Projects.Agents;
using Azure.Identity;
using EmailAgent.Configuration;
using Microsoft.Extensions.Options;
using OpenAI.Responses;

namespace EmailAgent.Services;

/// <summary>
/// Implements <see cref="IAIAgentService"/> using Azure AI Foundry.
///
/// On first use the service:
///   1. Creates an <see cref="AIProjectClient"/> authenticated with <see cref="DefaultAzureCredential"/>.
///   2. Registers (or updates) a <see cref="DeclarativeAgentDefinition"/> in the Foundry
///      Agent Administration with an <see cref="AzureAISearchTool"/> (Azure Blob + SharePoint
///      documents indexed in Azure AI Search) and, optionally, a
///      <see cref="SharepointPreviewTool"/> for direct SharePoint grounding.
///   3. Obtains a <see cref="ProjectResponsesClient"/> bound to the registered agent.
///
/// Subsequent calls to <see cref="ProcessEmailAsync"/> use the Responses API to
/// send the email content to the agent and return the generated reply text.
/// </summary>
public sealed class AIAgentService : IAIAgentService
{
    private readonly AIFoundrySettings _settings;
    private readonly ILogger<AIAgentService> _logger;

    private ProjectResponsesClient? _responsesClient;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    // System instructions that shape how the agent handles incoming emails.
    private const string AgentInstructions =
        """
        You are a professional email response assistant for a customer-support team.
        When given an incoming email (including the sender details, subject, and body),
        you must:
          1. Understand the customer's question or issue.
          2. Search the available knowledge sources (SharePoint pages and Azure Blob
             Storage documents indexed in Azure AI Search) for accurate, up-to-date
             information that addresses the question.
          3. Compose a polite, concise, and helpful reply in the same language as the
             incoming email.  Reference the information you found; do not invent facts.
          4. Use a professional tone and sign off as "Support Team".
        Return only the body text of the reply – do not include a subject line or headers.
        """;

    public AIAgentService(
        IOptions<AIFoundrySettings> settings,
        ILogger<AIAgentService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<string> ProcessEmailAsync(
        string subject,
        string bodyText,
        string senderName,
        string senderAddress,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        // Format the email as a single user prompt so the agent has full context.
        string prompt =
            $"""
            From: {senderName} <{senderAddress}>
            Subject: {subject}

            {bodyText}
            """;

        _logger.LogInformation(
            "Sending email from '{Sender}' (subject: '{Subject}') to AI agent for processing.",
            senderAddress, subject);

        ClientResult<ResponseResult> result = await _responsesClient!
            .CreateResponseAsync(prompt, previousResponseId: null, cancellationToken)
            .ConfigureAwait(false);

        string reply = result.Value.GetOutputText() ?? string.Empty;

        _logger.LogInformation(
            "AI agent generated a reply ({Length} chars) for message from '{Sender}'.",
            reply.Length, senderAddress);

        return reply;
    }

    // -----------------------------------------------------------------------
    // Initialisation (lazy, thread-safe)
    // -----------------------------------------------------------------------

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_responsesClient is not null)
            return;

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_responsesClient is not null)
                return;

            _logger.LogInformation(
                "Initialising Azure AI Foundry client (endpoint: {Endpoint}).",
                _settings.ProjectEndpoint);

            var projectClient = new AIProjectClient(
                new Uri(_settings.ProjectEndpoint),
                new DefaultAzureCredential());

            await RegisterAgentAsync(projectClient, cancellationToken).ConfigureAwait(false);

            var agentRef = (AgentReference)_settings.AgentName;

            _responsesClient = projectClient.ProjectOpenAIClient
                .GetProjectResponsesClientForAgent(agentRef, defaultConversationId: null);

            _logger.LogInformation(
                "AI Foundry agent '{AgentName}' is ready.", _settings.AgentName);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task RegisterAgentAsync(
        AIProjectClient projectClient,
        CancellationToken cancellationToken)
    {
        // Build the list of tools the agent will use for knowledge retrieval.
        var tools = new List<ProjectsAgentTool>();

        // Azure AI Search – queries the search index that covers both
        // Azure Blob Storage documents and SharePoint content.
        if (!string.IsNullOrWhiteSpace(_settings.AISearchConnectionId) &&
            !string.IsNullOrWhiteSpace(_settings.AISearchIndexName))
        {
            tools.Add(ProjectsAgentTool.CreateAzureAISearchTool(
                new AzureAISearchToolOptions(
                [
                    new AzureAISearchToolIndex
                    {
                        ProjectConnectionId = _settings.AISearchConnectionId,
                        IndexName = _settings.AISearchIndexName,
                    }
                ])));

            _logger.LogInformation(
                "AI Search tool configured (connection: {ConnId}, index: {Index}).",
                _settings.AISearchConnectionId, _settings.AISearchIndexName);
        }

        // SharePoint grounding tool – provides direct SharePoint site grounding
        // in addition to (or instead of) the AI Search index.
        if (!string.IsNullOrWhiteSpace(_settings.SharePointConnectionId))
        {
            var spOptions = new SharePointGroundingToolOptions();
            spOptions.ProjectConnections.Add(
                new ToolProjectConnection(_settings.SharePointConnectionId));

            tools.Add(ProjectsAgentTool.CreateSharepointTool(spOptions));

            _logger.LogInformation(
                "SharePoint grounding tool configured (connection: {ConnId}).",
                _settings.SharePointConnectionId);
        }

        // Declare the agent with its model, instructions, and tools.
        var agentDefinition = new DeclarativeAgentDefinition(_settings.ModelDeploymentName)
        {
            Instructions = AgentInstructions,
        };

        foreach (var tool in tools)
            agentDefinition.Tools.Add(tool);

        var options = new ProjectsAgentVersionCreationOptions(agentDefinition)
        {
            Description =
                "Email-processing agent that uses Azure AI Search (Blob + SharePoint) " +
                "to compose accurate customer-support replies."
        };

        ProjectsAgentVersion agentVersion =
            await projectClient.AgentAdministrationClient
                .CreateAgentVersionAsync(
                    agentName: _settings.AgentName,
                    options: options,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

        _logger.LogInformation(
            "Registered agent '{Name}' version '{Version}' (id: {Id}) in Azure AI Foundry.",
            agentVersion.Name, agentVersion.Version, agentVersion.Id);
    }
}

#pragma warning restore AAIP001
