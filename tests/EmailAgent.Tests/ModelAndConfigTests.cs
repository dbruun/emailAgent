using Xunit;
using EmailAgent.Configuration;
using EmailAgent.Models;
using EmailAgent.Services;

namespace EmailAgent.Tests;

public class ModelAndConfigTests
{
    // -----------------------------------------------------------------------
    // EmailItem record
    // -----------------------------------------------------------------------

    [Fact]
    public void EmailItem_PropertiesRoundTrip()
    {
        var now = DateTimeOffset.UtcNow;
        var item = new EmailItem("id-1", "Subject", "Body", "a@b.com", "Alice", now);

        Assert.Equal("id-1", item.Id);
        Assert.Equal("Subject", item.Subject);
        Assert.Equal("Body", item.BodyText);
        Assert.Equal("a@b.com", item.SenderAddress);
        Assert.Equal("Alice", item.SenderName);
        Assert.Equal(now, item.ReceivedAt);
    }

    [Fact]
    public void EmailItem_EqualityByValue()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new EmailItem("id-1", "Sub", "Body", "a@b.com", "Alice", now);
        var b = new EmailItem("id-1", "Sub", "Body", "a@b.com", "Alice", now);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void EmailItem_InequalityOnDifferentId()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new EmailItem("id-1", "Sub", "Body", "a@b.com", "Alice", now);
        var b = new EmailItem("id-2", "Sub", "Body", "a@b.com", "Alice", now);

        Assert.NotEqual(a, b);
    }

    // -----------------------------------------------------------------------
    // GraphContext record
    // -----------------------------------------------------------------------

    [Fact]
    public void GraphContext_PropertiesRoundTrip()
    {
        var issues = new List<PriorIssue>
        {
            new("Sub", "Sum", "open", DateTimeOffset.UtcNow, null)
        };
        var ctx = new GraphContext("Org", "gold", 5, issues, ["topic1"]);

        Assert.Equal("Org", ctx.OrganizationName);
        Assert.Equal("gold", ctx.SupportTier);
        Assert.Equal(5, ctx.PriorEmailCount);
        Assert.Single(ctx.PriorIssues);
        Assert.Single(ctx.KnownTopics);
    }

    [Fact]
    public void GraphContext_NullableFieldsDefault()
    {
        var ctx = new GraphContext(null, null, 0, [], []);

        Assert.Null(ctx.OrganizationName);
        Assert.Null(ctx.SupportTier);
    }

    // -----------------------------------------------------------------------
    // PriorIssue record
    // -----------------------------------------------------------------------

    [Fact]
    public void PriorIssue_PropertiesRoundTrip()
    {
        var now = DateTimeOffset.UtcNow;
        var issue = new PriorIssue("Subject", "Summary", "resolved", now, "Fixed");

        Assert.Equal("Subject", issue.Subject);
        Assert.Equal("Summary", issue.Summary);
        Assert.Equal("resolved", issue.Status);
        Assert.Equal(now, issue.CreatedAt);
        Assert.Equal("Fixed", issue.ResolutionSummary);
    }

    [Fact]
    public void PriorIssue_NullResolution()
    {
        var issue = new PriorIssue("Sub", "Sum", "open", DateTimeOffset.UtcNow, null);
        Assert.Null(issue.ResolutionSummary);
    }

    // -----------------------------------------------------------------------
    // ExtractedEntities record
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtractedEntities_PropertiesRoundTrip()
    {
        var entities = new ExtractedEntities(
            "example.com", "Example Inc", ["billing", "auth"], "Billing issue", "billing");

        Assert.Equal("example.com", entities.SenderDomain);
        Assert.Equal("Example Inc", entities.OrganizationName);
        Assert.Equal(2, entities.Topics.Count);
        Assert.Equal("Billing issue", entities.IssueSummary);
        Assert.Equal("billing", entities.PrimaryTopic);
    }

    [Fact]
    public void ExtractedEntities_NullOrganization()
    {
        var entities = new ExtractedEntities("d.com", null, [], "Issue", "general");
        Assert.Null(entities.OrganizationName);
    }

    // -----------------------------------------------------------------------
    // Configuration defaults
    // -----------------------------------------------------------------------

    [Fact]
    public void EmailProcessingSettings_Defaults()
    {
        var settings = new EmailProcessingSettings();

        Assert.Equal(30, settings.PollingIntervalSeconds);
        Assert.Equal("Processed", settings.ProcessedFolderName);
    }

    [Fact]
    public void GraphSettings_Defaults()
    {
        var settings = new GraphSettings();

        Assert.Equal(string.Empty, settings.TenantId);
        Assert.Equal(string.Empty, settings.ClientId);
        Assert.Equal(string.Empty, settings.ClientSecret);
        Assert.Equal(string.Empty, settings.UserEmail);
    }

    [Fact]
    public void AIFoundrySettings_Defaults()
    {
        var settings = new AIFoundrySettings();

        Assert.Equal(string.Empty, settings.ProjectEndpoint);
        Assert.Equal(string.Empty, settings.ModelDeploymentName);
        Assert.Equal(string.Empty, settings.AISearchConnectionId);
        Assert.Equal(string.Empty, settings.AISearchIndexName);
        Assert.Equal(string.Empty, settings.SharePointConnectionId);
        Assert.Equal("email-agent", settings.AgentName);
    }

    [Fact]
    public void GraphRagSettings_Defaults()
    {
        var settings = new GraphRagSettings();

        Assert.Equal(string.Empty, settings.GremlinEndpoint);
        Assert.Equal(string.Empty, settings.DatabaseName);
        Assert.Equal(string.Empty, settings.GraphName);
        Assert.Equal(string.Empty, settings.AccountKey);
        Assert.Equal("email-agent", settings.PartitionKey);
        Assert.Equal(3, settings.MaxRelatedIssues);
        Assert.True(settings.Enabled);
    }
}
