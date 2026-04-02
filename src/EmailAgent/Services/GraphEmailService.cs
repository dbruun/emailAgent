using Azure.Identity;
using EmailAgent.Configuration;
using EmailAgent.Models;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Users.Item.Messages.Item.Move;
using Microsoft.Graph.Users.Item.Messages.Item.Reply;

namespace EmailAgent.Services;

/// <summary>
/// Implements <see cref="IGraphEmailService"/> using the Microsoft Graph SDK v5.
/// Authenticates with app-only permissions via a client-secret credential so the
/// service can run unattended as a background worker.
/// </summary>
public sealed class GraphEmailService : IGraphEmailService
{
    private readonly GraphSettings _settings;
    private readonly ILogger<GraphEmailService> _logger;
    private readonly GraphServiceClient _graphClient;

    // Cache folder IDs to avoid redundant Graph calls on every poll iteration.
    private readonly Dictionary<string, string> _folderIdCache = new(StringComparer.OrdinalIgnoreCase);

    public GraphEmailService(
        IOptions<GraphSettings> settings,
        ILogger<GraphEmailService> logger)
    {
        _settings = settings.Value;
        _logger = logger;

        var credential = new ClientSecretCredential(
            _settings.TenantId,
            _settings.ClientId,
            _settings.ClientSecret);

        _graphClient = new GraphServiceClient(credential,
            ["https://graph.microsoft.com/.default"]);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<EmailItem>> GetUnreadEmailsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _graphClient.Users[_settings.UserEmail].Messages
            .GetAsync(config =>
            {
                config.QueryParameters.Filter = "isRead eq false";
                config.QueryParameters.Select =
                [
                    "id", "subject", "body",
                    "sender", "receivedDateTime"
                ];
                config.QueryParameters.Top = 50;
            }, cancellationToken)
            .ConfigureAwait(false);

        if (result?.Value is null || result.Value.Count == 0)
            return [];

        var emails = new List<EmailItem>(result.Value.Count);
        foreach (var msg in result.Value)
        {
            emails.Add(new EmailItem(
                Id: msg.Id ?? string.Empty,
                Subject: msg.Subject ?? "(no subject)",
                BodyText: msg.Body?.Content ?? string.Empty,
                SenderAddress: msg.Sender?.EmailAddress?.Address ?? string.Empty,
                SenderName: msg.Sender?.EmailAddress?.Name ?? string.Empty,
                ReceivedAt: msg.ReceivedDateTime ?? DateTimeOffset.UtcNow));
        }

        _logger.LogInformation("Retrieved {Count} unread email(s) from inbox.", emails.Count);
        return emails;
    }

    /// <inheritdoc/>
    public async Task SendReplyAsync(
        string messageId,
        string replyBody,
        CancellationToken cancellationToken = default)
    {
        await _graphClient.Users[_settings.UserEmail].Messages[messageId].Reply
            .PostAsync(new ReplyPostRequestBody { Comment = replyBody }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Sent reply for message {MessageId}.", messageId);
    }

    /// <inheritdoc/>
    public async Task MoveToFolderAsync(
        string messageId,
        string folderName,
        CancellationToken cancellationToken = default)
    {
        string folderId = await GetFolderIdAsync(folderName, cancellationToken)
            .ConfigureAwait(false);

        await _graphClient.Users[_settings.UserEmail].Messages[messageId].Move
            .PostAsync(new MovePostRequestBody { DestinationId = folderId },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Moved message {MessageId} to folder '{FolderName}'.", messageId, folderName);
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private async Task<string> GetFolderIdAsync(string folderName, CancellationToken cancellationToken)
    {
        if (_folderIdCache.TryGetValue(folderName, out string? cachedId))
            return cachedId;

        var folders = await _graphClient.Users[_settings.UserEmail].MailFolders
            .GetAsync(config =>
            {
                // Escape single quotes in the folder name to prevent OData filter injection.
                string safeName = folderName.Replace("'", "''");
                config.QueryParameters.Filter = $"displayName eq '{safeName}'";
                config.QueryParameters.Select = ["id", "displayName"];
            }, cancellationToken)
            .ConfigureAwait(false);

        var folder = folders?.Value?.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Mailbox folder '{folderName}' was not found in the inbox. " +
                "Please create the folder before starting the agent.");

        _folderIdCache[folderName] = folder.Id!;
        return folder.Id!;
    }
}
