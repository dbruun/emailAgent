namespace EmailAgent.Configuration;

/// <summary>
/// Configuration settings that control the email-monitoring behaviour.
/// Bind this section from appsettings.json under the "EmailProcessing" key.
/// </summary>
public sealed class EmailProcessingSettings
{
    /// <summary>
    /// How often (in seconds) the worker polls the inbox for new unread messages.
    /// Defaults to 30 seconds.
    /// </summary>
    public int PollingIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Display name of the existing mailbox folder that processed emails are
    /// moved into after a reply has been sent.  The folder must already exist
    /// in the monitored mailbox.  Defaults to "Processed".
    /// </summary>
    public string ProcessedFolderName { get; set; } = "Processed";
}
