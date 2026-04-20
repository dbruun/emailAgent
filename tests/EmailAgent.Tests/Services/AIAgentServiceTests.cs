using Xunit;
using System.Reflection;
using EmailAgent.Models;
using EmailAgent.Services;

namespace EmailAgent.Tests.Services;

/// <summary>
/// Tests for <see cref="AIAgentService"/>.
/// The public method <c>ProcessEmailAsync</c> requires real Azure credentials and
/// cannot be unit-tested without an integration environment. These tests cover the
/// pure <c>BuildGraphContextBlock</c> helper via reflection.
/// </summary>
public class AIAgentServiceTests
{
    private static readonly MethodInfo BuildGraphContextBlockMethod =
        typeof(AIAgentService).GetMethod("BuildGraphContextBlock",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static string InvokeBuildGraphContextBlock(GraphContext? ctx) =>
        (string)BuildGraphContextBlockMethod.Invoke(null, [ctx])!;

    [Fact]
    public void NullContext_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, InvokeBuildGraphContextBlock(null));
    }

    [Fact]
    public void WithOrganizationName_IncludesOrg()
    {
        var ctx = new GraphContext("Contoso Ltd", null, 0, [], []);
        string result = InvokeBuildGraphContextBlock(ctx);

        Assert.Contains("Contoso Ltd", result);
        Assert.Contains("CUSTOMER RELATIONSHIP CONTEXT", result);
    }

    [Fact]
    public void WithSupportTier_IncludesTier()
    {
        var ctx = new GraphContext("Contoso Ltd", "premium", 0, [], []);
        string result = InvokeBuildGraphContextBlock(ctx);

        Assert.Contains("premium tier", result);
    }

    [Fact]
    public void WithPriorEmailCount_IncludesCount()
    {
        var ctx = new GraphContext(null, null, 7, [], []);
        string result = InvokeBuildGraphContextBlock(ctx);

        Assert.Contains("7 previous email(s)", result);
    }

    [Fact]
    public void ZeroPriorEmailCount_OmitsCountLine()
    {
        var ctx = new GraphContext("Org", null, 0, [], []);
        string result = InvokeBuildGraphContextBlock(ctx);

        Assert.DoesNotContain("previous email(s)", result);
    }

    [Fact]
    public void WithPriorIssues_ListsIssues()
    {
        var issues = new List<PriorIssue>
        {
            new("Login broken", "Cannot sign in", "resolved",
                new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero), "Password reset"),
            new("Billing error", "Overcharged", "open",
                new DateTimeOffset(2025, 7, 15, 0, 0, 0, TimeSpan.Zero), null),
        };
        var ctx = new GraphContext(null, null, 0, issues, []);
        string result = InvokeBuildGraphContextBlock(ctx);

        Assert.Contains("Login broken", result);
        Assert.Contains("Billing error", result);
        Assert.Contains("RESOLVED", result);
        Assert.Contains("OPEN", result);
        Assert.Contains("Password reset", result);
    }

    [Fact]
    public void WithKnownTopics_ListsTopics()
    {
        var ctx = new GraphContext(null, null, 0, [], ["billing", "authentication"]);
        string result = InvokeBuildGraphContextBlock(ctx);

        Assert.Contains("billing", result);
        Assert.Contains("authentication", result);
        Assert.Contains("Known topics", result);
    }

    [Fact]
    public void FullContext_ContainsAllSections()
    {
        var issues = new List<PriorIssue>
        {
            new("Issue 1", "Summary 1", "resolved",
                DateTimeOffset.UtcNow, "Fixed it")
        };
        var ctx = new GraphContext("Acme Corp", "enterprise", 10, issues, ["networking"]);
        string result = InvokeBuildGraphContextBlock(ctx);

        Assert.Contains("Acme Corp", result);
        Assert.Contains("enterprise tier", result);
        Assert.Contains("10 previous email(s)", result);
        Assert.Contains("Issue 1", result);
        Assert.Contains("Fixed it", result);
        Assert.Contains("networking", result);
        Assert.Contains("=== CUSTOMER RELATIONSHIP CONTEXT ===", result);
        Assert.Contains("======================================", result);
    }

    [Fact]
    public void EmptyContext_StillProducesHeaderAndFooter()
    {
        var ctx = new GraphContext(null, null, 0, [], []);
        string result = InvokeBuildGraphContextBlock(ctx);

        // Even with no data, the header and footer should be present
        Assert.Contains("=== CUSTOMER RELATIONSHIP CONTEXT ===", result);
        Assert.Contains("======================================", result);
    }

    [Fact]
    public void OrganizationWithoutTier_NoTierSuffix()
    {
        var ctx = new GraphContext("Solo Corp", null, 0, [], []);
        string result = InvokeBuildGraphContextBlock(ctx);

        Assert.Contains("Solo Corp", result);
        Assert.DoesNotContain("tier", result);
    }
}
