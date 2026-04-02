namespace EmailAgent.Models;

/// <summary>
/// An immutable snapshot of a single email message retrieved from the inbox.
/// </summary>
/// <param name="Id">The unique Graph message ID.</param>
/// <param name="Subject">Subject line of the email.</param>
/// <param name="BodyText">Plain-text (or HTML-stripped) body of the email.</param>
/// <param name="SenderAddress">SMTP address of the sender.</param>
/// <param name="SenderName">Display name of the sender.</param>
/// <param name="ReceivedAt">Timestamp when the message arrived.</param>
public sealed record EmailItem(
    string Id,
    string Subject,
    string BodyText,
    string SenderAddress,
    string SenderName,
    DateTimeOffset ReceivedAt);
