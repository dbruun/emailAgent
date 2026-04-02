using EmailAgent.Models;

namespace EmailAgent.Services;

/// <summary>
/// Abstraction over Microsoft Graph for email-related operations
/// (reading, replying, and sorting mail).
/// </summary>
public interface IGraphEmailService
{
    /// <summary>
    /// Returns all unread messages currently in the monitored inbox.
    /// </summary>
    Task<IReadOnlyList<EmailItem>> GetUnreadEmailsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a reply to the specified message.
    /// </summary>
    /// <param name="messageId">Graph message ID to reply to.</param>
    /// <param name="replyBody">Plain-text body of the reply.</param>
    Task SendReplyAsync(string messageId, string replyBody, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the specified message into a named mailbox folder and marks it as read.
    /// </summary>
    /// <param name="messageId">Graph message ID to move.</param>
    /// <param name="folderName">Display name of the destination folder.</param>
    Task MoveToFolderAsync(string messageId, string folderName, CancellationToken cancellationToken = default);
}
