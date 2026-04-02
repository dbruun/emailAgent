namespace EmailAgent.Configuration;

/// <summary>
/// Configuration settings for connecting to Microsoft Graph (Outlook / Exchange).
/// Bind this section from appsettings.json under the "Graph" key.
/// </summary>
public sealed class GraphSettings
{
    /// <summary>Azure AD tenant (directory) ID.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Azure AD application (client) ID used for app-only auth.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Azure AD client secret for the application.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>UPN or SMTP address of the mailbox to monitor (e.g. support@contoso.com).</summary>
    public string UserEmail { get; set; } = string.Empty;
}
