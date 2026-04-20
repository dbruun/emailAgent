using EmailAgent.Configuration;
using EmailAgent.Models;
using EmailAgent.Services;
using Microsoft.Extensions.Options;

namespace EmailAgent.Workers;

/// <summary>
/// Long-running background service that polls an Outlook mailbox for unread messages,
/// uses the Azure AI Foundry agent to generate a reply, sends the reply via Microsoft
/// Graph, and moves the processed message to a designated folder.
/// </summary>
public sealed class EmailMonitorWorker : BackgroundService
{
    private readonly IGraphEmailService _graphEmailService;
    private readonly IAIAgentService _aiAgentService;
    private readonly IGraphRagService _graphRagService;
    private readonly EmailProcessingSettings _settings;
    private readonly GraphRagSettings _graphRagSettings;
    private readonly ILogger<EmailMonitorWorker> _logger;

    public EmailMonitorWorker(
        IGraphEmailService graphEmailService,
        IAIAgentService aiAgentService,
        IGraphRagService graphRagService,
        IOptions<EmailProcessingSettings> settings,
        IOptions<GraphRagSettings> graphRagSettings,
        ILogger<EmailMonitorWorker> logger)
    {
        _graphEmailService = graphEmailService;
        _aiAgentService = aiAgentService;
        _graphRagService = graphRagService;
        _settings = settings.Value;
        _graphRagSettings = graphRagSettings.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Email monitor worker started. Polling every {Interval}s; " +
            "processed messages will be moved to '{Folder}'.",
            _settings.PollingIntervalSeconds,
            _settings.ProcessedFolderName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessUnreadEmailsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error while processing emails. Will retry after the polling interval.");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(_settings.PollingIntervalSeconds),
                stoppingToken)
                .ConfigureAwait(false);
        }

        _logger.LogInformation("Email monitor worker stopping.");
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private async Task ProcessUnreadEmailsAsync(CancellationToken cancellationToken)
    {
        var emails = await _graphEmailService
            .GetUnreadEmailsAsync(cancellationToken)
            .ConfigureAwait(false);

        if (emails.Count == 0)
        {
            _logger.LogDebug("No unread emails found during this poll cycle.");
            return;
        }

        _logger.LogInformation(
            "Found {Count} unread email(s) to process.", emails.Count);

        foreach (var email in emails)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            await ProcessSingleEmailAsync(email, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessSingleEmailAsync(
        Models.EmailItem email,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Processing email from '{Sender}' – subject: '{Subject}'.",
            email.SenderAddress, email.Subject);

        try
        {
            GraphContext? graphContext = null;

            if (_graphRagSettings.Enabled)
            {
                try
                {
                    var entities = await _graphRagService
                        .ExtractEntitiesAsync(email, cancellationToken)
                        .ConfigureAwait(false);

                    await _graphRagService
                        .UpsertGraphAsync(email, entities, cancellationToken)
                        .ConfigureAwait(false);

                    graphContext = await _graphRagService
                        .GetGraphContextAsync(email.SenderAddress, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "GraphRAG preprocessing failed for email {MessageId}. Continuing with standard prompt.",
                        email.Id);
                }
            }

            // 1. Generate a reply with the AI agent.
            string replyBody = await _aiAgentService.ProcessEmailAsync(
                email.Subject,
                email.BodyText,
                email.SenderName,
                email.SenderAddress,
                graphContext,
                cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(replyBody))
            {
                _logger.LogWarning(
                    "AI agent returned an empty reply for email {MessageId}. Skipping reply and move.",
                    email.Id);
                return;
            }

            // 2. Send the reply through Microsoft Graph.
            await _graphEmailService.SendReplyAsync(email.Id, replyBody, cancellationToken)
                .ConfigureAwait(false);

            if (_graphRagSettings.Enabled)
            {
                try
                {
                    await _graphRagService
                        .RecordResolutionAsync(email.Id, replyBody, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "GraphRAG resolution tracking failed for email {MessageId}.",
                        email.Id);
                }
            }

            // 3. Move the message into the processed folder.
            await _graphEmailService.MoveToFolderAsync(
                email.Id,
                _settings.ProcessedFolderName,
                cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Successfully processed email {MessageId} from '{Sender}'.",
                email.Id, email.SenderAddress);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Failed to process email {MessageId} from '{Sender}'. " +
                "The message will be retried on the next poll cycle.",
                email.Id, email.SenderAddress);
        }
    }
}
