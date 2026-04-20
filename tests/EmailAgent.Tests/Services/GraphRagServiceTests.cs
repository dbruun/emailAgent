using Xunit;
using System.Reflection;
using EmailAgent.Configuration;
using EmailAgent.Models;
using EmailAgent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EmailAgent.Tests.Services;

public class GraphRagServiceTests
{
    private static GraphRagService CreateService(bool enabled = false) =>
        new(
            Options.Create(new GraphRagSettings { Enabled = enabled }),
            Options.Create(new AIFoundrySettings()),
            NullLogger<GraphRagService>.Instance);

    private static EmailItem TestEmail(
        string senderAddress = "alice@example.com",
        string subject = "Help needed",
        string body = "I need help with billing") =>
        new("msg-1", subject, body, senderAddress, "Alice", DateTimeOffset.UtcNow);

    // -----------------------------------------------------------------------
    // Disabled paths – should return immediately / return fallback
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ExtractEntitiesAsync_WhenDisabled_ReturnsFallbackEntities()
    {
        var service = CreateService(enabled: false);
        var email = TestEmail();

        var result = await service.ExtractEntitiesAsync(email);

        Assert.Equal("example.com", result.SenderDomain);
        Assert.Null(result.OrganizationName);
        Assert.Equal(email.Subject, result.IssueSummary);
        Assert.Single(result.Topics);
    }

    [Fact]
    public async Task ExtractEntitiesAsync_WhenDisabled_NormalizesSubjectAsTopic()
    {
        var service = CreateService(enabled: false);
        var email = TestEmail(subject: "Billing Issue!!!");

        var result = await service.ExtractEntitiesAsync(email);

        Assert.Equal("billing-issue", result.PrimaryTopic);
        Assert.Contains("billing-issue", result.Topics);
    }

    [Fact]
    public async Task ExtractEntitiesAsync_WhenDisabled_EmptySubject_UsesGeneral()
    {
        var service = CreateService(enabled: false);
        var email = TestEmail(subject: "   ");

        var result = await service.ExtractEntitiesAsync(email);

        Assert.Equal("general", result.PrimaryTopic);
    }

    [Fact]
    public async Task UpsertGraphAsync_WhenDisabled_ReturnsImmediately()
    {
        var service = CreateService(enabled: false);
        var email = TestEmail();
        var entities = new ExtractedEntities("example.com", null, [], "Issue", "general");

        // Should not throw
        await service.UpsertGraphAsync(email, entities);
    }

    [Fact]
    public async Task GetGraphContextAsync_WhenDisabled_ReturnsEmptyContext()
    {
        var service = CreateService(enabled: false);

        var result = await service.GetGraphContextAsync("alice@example.com");

        Assert.Null(result.OrganizationName);
        Assert.Null(result.SupportTier);
        Assert.Equal(0, result.PriorEmailCount);
        Assert.Empty(result.PriorIssues);
        Assert.Empty(result.KnownTopics);
    }

    [Fact]
    public async Task RecordResolutionAsync_WhenDisabled_ReturnsImmediately()
    {
        var service = CreateService(enabled: false);

        // Should not throw
        await service.RecordResolutionAsync("msg-1", "Reply body");
    }

    [Fact]
    public async Task DisposeAsync_WhenNotInitialized_DoesNotThrow()
    {
        var service = CreateService(enabled: false);
        await service.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // Fallback entity extraction (domain parsing, topic normalization)
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("user@contoso.com", "contoso.com")]
    [InlineData("support@sub.domain.co.uk", "sub.domain.co.uk")]
    [InlineData("no-at-sign", "unknown")]
    [InlineData("trailing@", "unknown")]
    [InlineData("", "unknown")]
    public async Task FallbackEntities_ExtractsDomainCorrectly(string address, string expected)
    {
        var service = CreateService(enabled: false);
        var email = TestEmail(senderAddress: address);

        var result = await service.ExtractEntitiesAsync(email);

        Assert.Equal(expected, result.SenderDomain);
    }

    // -----------------------------------------------------------------------
    // Private static helper tests via reflection
    // -----------------------------------------------------------------------

    private static readonly MethodInfo StripCodeFenceMethod =
        typeof(GraphRagService).GetMethod("StripCodeFence",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo NormalizeTopicMethod =
        typeof(GraphRagService).GetMethod("NormalizeTopic",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo GetDomainMethod =
        typeof(GraphRagService).GetMethod("GetDomain",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo GetMapStringMethod =
        typeof(GraphRagService).GetMethod("GetMapString",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static string InvokeStripCodeFence(string json) =>
        (string)StripCodeFenceMethod.Invoke(null, [json])!;

    private static string InvokeNormalizeTopic(string value) =>
        (string)NormalizeTopicMethod.Invoke(null, [value])!;

    private static string InvokeGetDomain(string address) =>
        (string)GetDomainMethod.Invoke(null, [address])!;

    private static string? InvokeGetMapString(IDictionary<object, object> map, string key) =>
        (string?)GetMapStringMethod.Invoke(null, [map, key]);

    [Theory]
    [InlineData("{}", "{}")]
    [InlineData("```json\n{}\n```", "{}")]
    [InlineData("```\n{}\n```", "{}")]
    [InlineData("no fence", "no fence")]
    public void StripCodeFence_HandlesVariousFormats(string input, string expected)
    {
        Assert.Equal(expected, InvokeStripCodeFence(input));
    }

    [Theory]
    [InlineData("Hello World", "hello-world")]
    [InlineData("billing-issue", "billing-issue")]
    [InlineData("UPPER CASE", "upper-case")]
    [InlineData("  spaces  ", "spaces")]
    [InlineData("special!@#chars", "special-chars")]
    [InlineData("", "")]
    [InlineData("already-kebab", "already-kebab")]
    [InlineData("multiple   spaces", "multiple-spaces")]
    public void NormalizeTopic_ProducesKebabCase(string input, string expected)
    {
        Assert.Equal(expected, InvokeNormalizeTopic(input));
    }

    [Theory]
    [InlineData("user@example.com", "example.com")]
    [InlineData("a@b", "b")]
    [InlineData("noatsign", "unknown")]
    [InlineData("trailing@", "unknown")]
    public void GetDomain_ParsesCorrectly(string address, string expected)
    {
        Assert.Equal(expected, InvokeGetDomain(address));
    }

    [Fact]
    public void GetMapString_ScalarValue_ReturnsString()
    {
        var map = new Dictionary<object, object> { ["name"] = "TestOrg" };
        Assert.Equal("TestOrg", InvokeGetMapString(map, "name"));
    }

    [Fact]
    public void GetMapString_ListValue_ReturnsFirstElement()
    {
        var map = new Dictionary<object, object>
        {
            ["name"] = new List<object> { "First", "Second" }
        };
        Assert.Equal("First", InvokeGetMapString(map, "name"));
    }

    [Fact]
    public void GetMapString_EmptyList_ReturnsNull()
    {
        var map = new Dictionary<object, object>
        {
            ["name"] = new List<object>()
        };
        Assert.Null(InvokeGetMapString(map, "name"));
    }

    [Fact]
    public void GetMapString_MissingKey_ReturnsNull()
    {
        var map = new Dictionary<object, object>();
        Assert.Null(InvokeGetMapString(map, "missing"));
    }

    [Fact]
    public void GetMapString_NullValue_ReturnsNull()
    {
        var map = new Dictionary<object, object> { ["name"] = null! };
        Assert.Null(InvokeGetMapString(map, "name"));
    }

    // -----------------------------------------------------------------------
    // Enabled but missing config → throws on first graph call
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GetGraphContextAsync_EnabledButNoConfig_ThrowsInvalidOperation()
    {
        var service = new GraphRagService(
            Options.Create(new GraphRagSettings
            {
                Enabled = true,
                GremlinEndpoint = "",
                AccountKey = ""
            }),
            Options.Create(new AIFoundrySettings()),
            NullLogger<GraphRagService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetGraphContextAsync("test@example.com"));
    }
}
