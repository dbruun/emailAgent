#pragma warning disable OPENAI001

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.ClientModel;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.Identity;
using EmailAgent.Configuration;
using EmailAgent.Models;
using Gremlin.Net.Driver;
using Microsoft.Extensions.Options;
using OpenAI.Responses;

namespace EmailAgent.Services;

/// <summary>
/// GraphRAG implementation backed by Azure Cosmos DB for Apache Gremlin.
/// </summary>
public sealed class GraphRagService : IGraphRagService, IAsyncDisposable
{
    private readonly GraphRagSettings _settings;
    private readonly AIFoundrySettings _foundrySettings;
    private readonly ILogger<GraphRagService> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private GremlinClient? _gremlinClient;
    private const int MaxSnippetLength = 500;

    private const string ExtractionSystemPrompt =
        """
        You are an entity extractor for a customer support email system.
        Given an email, return a single JSON object with exactly these fields:

        {
          "senderDomain": "<domain part of the From address>",
          "organizationName": "<inferred company name, or null>",
          "topics": ["<normalized topic 1>", "<normalized topic 2>"],
          "issueSummary": "<one sentence, <=15 words>",
          "primaryTopic": "<single most important topic>"
        }

        Normalize topics to lowercase kebab-case.
        Return valid JSON only. Do not include any other text.
        """;

    public GraphRagService(
        IOptions<GraphRagSettings> settings,
        IOptions<AIFoundrySettings> foundrySettings,
        ILogger<GraphRagService> logger)
    {
        _settings = settings.Value;
        _foundrySettings = foundrySettings.Value;
        _logger = logger;
    }

    public async Task<ExtractedEntities> ExtractEntitiesAsync(
        EmailItem email,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
            return BuildFallbackEntities(email);

        string emailSnippet =
            $"From: {email.SenderName} <{email.SenderAddress}>\n" +
            $"Subject: {email.Subject}\n" +
            $"Body (first {MaxSnippetLength} chars): {email.BodyText[..Math.Min(MaxSnippetLength, email.BodyText.Length)]}";

        var projectClient = new AIProjectClient(
            new Uri(_foundrySettings.ProjectEndpoint),
            new DefaultAzureCredential());

        var agentRef = (AgentReference)_foundrySettings.AgentName;
        var responsesClient = projectClient.ProjectOpenAIClient
            .GetProjectResponsesClientForAgent(agentRef, defaultConversationId: null);

        string extractionPrompt =
            $"""
            {ExtractionSystemPrompt}

            Email:
            {emailSnippet}
            """;

        ClientResult<ResponseResult> completion = await responsesClient
            .CreateResponseAsync(extractionPrompt, previousResponseId: null, cancellationToken)
            .ConfigureAwait(false);

        string json = (completion.Value.GetOutputText() ?? string.Empty).Trim();
        json = StripCodeFence(json);

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            var topics = root.TryGetProperty("topics", out var topicsEl)
                ? topicsEl.EnumerateArray()
                    .Select(static t => t.GetString() ?? string.Empty)
                    .Select(NormalizeTopic)
                    .Where(static t => t.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Array.Empty<string>();

            string senderDomain = GetDomain(email.SenderAddress);
            if (root.TryGetProperty("senderDomain", out var senderDomainEl))
            {
                string? parsedSenderDomain = senderDomainEl.GetString();
                if (!string.IsNullOrWhiteSpace(parsedSenderDomain))
                    senderDomain = parsedSenderDomain;
            }

            string issueSummary = root.TryGetProperty("issueSummary", out var issueSummaryEl)
                ? issueSummaryEl.GetString() ?? email.Subject
                : email.Subject;

            string primaryTopic = root.TryGetProperty("primaryTopic", out var primaryTopicEl)
                ? NormalizeTopic(primaryTopicEl.GetString() ?? "general")
                : "general";

            return new ExtractedEntities(
                SenderDomain: senderDomain,
                OrganizationName: root.TryGetProperty("organizationName", out var orgEl) &&
                                  orgEl.ValueKind != JsonValueKind.Null
                    ? orgEl.GetString()
                    : null,
                Topics: topics,
                IssueSummary: issueSummary,
                PrimaryTopic: primaryTopic);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse GraphRAG extraction JSON. Falling back to deterministic extraction.");
            return BuildFallbackEntities(email);
        }
    }

    public async Task UpsertGraphAsync(
        EmailItem email,
        ExtractedEntities entities,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
            return;

        GremlinClient client = await GetClientAsync(cancellationToken).ConfigureAwait(false);

        await client.SubmitAsync<dynamic>(
            "g.V(senderId).fold().coalesce(unfold(),addV('Sender')" +
            ".property(id,senderId).property('displayName',senderName)" +
            ".property('firstSeenUtc',receivedUtc).property('partitionKey',pk).property('emailCount',0))" +
            ".property('displayName',senderName).property('lastSeenUtc',receivedUtc)" +
            ".property('emailCount',coalesce(values('emailCount'),constant(0)).math('_+1'))",
            new Dictionary<string, object>
            {
                ["senderId"] = email.SenderAddress,
                ["senderName"] = email.SenderName,
                ["receivedUtc"] = email.ReceivedAt.ToString("O"),
                ["pk"] = _settings.PartitionKey,
            }).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(entities.SenderDomain))
        {
            string orgName = entities.OrganizationName ?? entities.SenderDomain;
            await client.SubmitAsync<dynamic>(
                "g.V(orgId).fold().coalesce(unfold(),addV('Organization')" +
                ".property(id,orgId).property('name',orgName).property('tier','community').property('partitionKey',pk))",
                new Dictionary<string, object>
                {
                    ["orgId"] = entities.SenderDomain,
                    ["orgName"] = orgName,
                    ["pk"] = _settings.PartitionKey,
                }).ConfigureAwait(false);

            await client.SubmitAsync<dynamic>(
                "g.V(senderId).coalesce(outE('BELONGS_TO').where(inV().hasId(orgId)),addE('BELONGS_TO')" +
                ".property('inferredAt',nowUtc).property('confidence',0.8).to(V(orgId)))",
                new Dictionary<string, object>
                {
                    ["senderId"] = email.SenderAddress,
                    ["orgId"] = entities.SenderDomain,
                    ["nowUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                }).ConfigureAwait(false);
        }

        await client.SubmitAsync<dynamic>(
            "g.V(issueId).fold().coalesce(unfold(),addV('Issue').property(id,issueId)" +
            ".property('subject',subject).property('summary',summary).property('status','open')" +
            ".property('createdUtc',createdUtc).property('partitionKey',pk))",
            new Dictionary<string, object>
            {
                ["issueId"] = email.Id,
                ["subject"] = email.Subject,
                ["summary"] = entities.IssueSummary,
                ["createdUtc"] = email.ReceivedAt.ToString("O"),
                ["pk"] = _settings.PartitionKey,
            }).ConfigureAwait(false);

        await client.SubmitAsync<dynamic>(
            "g.V(senderId).coalesce(outE('SUBMITTED').where(inV().hasId(issueId)),addE('SUBMITTED')" +
            ".property('emailId',issueId).property('submittedUtc',submittedUtc).to(V(issueId)))",
            new Dictionary<string, object>
            {
                ["senderId"] = email.SenderAddress,
                ["issueId"] = email.Id,
                ["submittedUtc"] = email.ReceivedAt.ToString("O"),
            }).ConfigureAwait(false);

        foreach (string topic in entities.Topics.Where(static t => !string.IsNullOrWhiteSpace(t)))
        {
            await client.SubmitAsync<dynamic>(
                "g.V(topicId).fold().coalesce(unfold(),addV('Topic').property(id,topicId)" +
                ".property('displayName',topicId).property('frequency',0).property('partitionKey',pk))" +
                ".property('frequency',coalesce(values('frequency'),constant(0)).math('_+1'))",
                new Dictionary<string, object>
                {
                    ["topicId"] = topic,
                    ["pk"] = _settings.PartitionKey,
                }).ConfigureAwait(false);

            await client.SubmitAsync<dynamic>(
                "g.V(issueId).coalesce(outE('HAS_TOPIC').where(inV().hasId(topicId)),addE('HAS_TOPIC')" +
                ".property('confidence',1.0).to(V(topicId)))",
                new Dictionary<string, object>
                {
                    ["issueId"] = email.Id,
                    ["topicId"] = topic,
                }).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Graph updated for sender '{Sender}' / issue '{MessageId}'.",
            email.SenderAddress, email.Id);
    }

    public async Task<GraphContext> GetGraphContextAsync(
        string senderAddress,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
            return EmptyContext();

        GremlinClient client = await GetClientAsync(cancellationToken).ConfigureAwait(false);

        string? orgName = null;
        string? supportTier = null;
        int priorEmailCount = 0;

        ResultSet<dynamic> orgResult = await client.SubmitAsync<dynamic>(
            "g.V(senderId).out('BELONGS_TO').valueMap('name','tier').limit(1)",
            new Dictionary<string, object> { ["senderId"] = senderAddress }).ConfigureAwait(false);

        var firstOrg = orgResult.FirstOrDefault();
        if (firstOrg is IDictionary<object, object> orgMap)
        {
            orgName = GetMapString(orgMap, "name");
            supportTier = GetMapString(orgMap, "tier");
        }

        ResultSet<dynamic> countResult = await client.SubmitAsync<dynamic>(
            "g.V(senderId).values('emailCount')",
            new Dictionary<string, object> { ["senderId"] = senderAddress }).ConfigureAwait(false);

        var firstCount = countResult.FirstOrDefault();
        if (firstCount is not null)
        {
            if (int.TryParse(firstCount.ToString(), out int parsedCount))
                priorEmailCount = parsedCount;
        }

        ResultSet<dynamic> priorIssuesResult = await client.SubmitAsync<dynamic>(
            "g.V(senderId).out('SUBMITTED').order().by('createdUtc',decr).limit(maxIssues)" +
            ".project('subject','summary','status','createdUtc','resolution')" +
            ".by(values('subject').fold()).by(values('summary').fold()).by(values('status').fold())" +
            ".by(values('createdUtc').fold()).by(out('RESOLVED_BY').values('summary').fold())",
            new Dictionary<string, object>
            {
                ["senderId"] = senderAddress,
                ["maxIssues"] = _settings.MaxRelatedIssues,
            }).ConfigureAwait(false);

        var priorIssues = new List<PriorIssue>(priorIssuesResult.Count);
        foreach (dynamic row in priorIssuesResult)
        {
            if (row is not IDictionary<object, object> issueMap)
                continue;

            string createdRaw = GetMapString(issueMap, "createdUtc") ?? string.Empty;
            DateTimeOffset createdAt = DateTimeOffset.TryParse(createdRaw, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;

            priorIssues.Add(new PriorIssue(
                Subject: GetMapString(issueMap, "subject") ?? string.Empty,
                Summary: GetMapString(issueMap, "summary") ?? string.Empty,
                Status: GetMapString(issueMap, "status") ?? string.Empty,
                CreatedAt: createdAt,
                ResolutionSummary: GetMapString(issueMap, "resolution")));
        }

        ResultSet<dynamic> topicsResult = await client.SubmitAsync<dynamic>(
            "g.V(senderId).out('SUBMITTED').out('HAS_TOPIC').dedup().values('displayName')",
            new Dictionary<string, object> { ["senderId"] = senderAddress }).ConfigureAwait(false);

        var topics = topicsResult
            .Select(static t => t?.ToString() ?? string.Empty)
            .Where(static t => t.Length > 0)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static t => t, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new GraphContext(
            OrganizationName: orgName,
            SupportTier: supportTier,
            PriorEmailCount: priorEmailCount,
            PriorIssues: priorIssues,
            KnownTopics: topics);
    }

    public async Task RecordResolutionAsync(
        string messageId,
        string replyBody,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled)
            return;

        GremlinClient client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        string snippet = replyBody[..Math.Min(MaxSnippetLength, replyBody.Length)];
        string resolutionId = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{messageId}:{snippet}")));
        string summary = snippet.Length > 100 ? snippet[..100] + "…" : snippet;
        string nowUtc = DateTimeOffset.UtcNow.ToString("O");

        await client.SubmitAsync<dynamic>(
            "g.V(resId).fold().coalesce(unfold(),addV('Resolution').property(id,resId)" +
            ".property('summary',summary).property('replySnippet',snippet)" +
            ".property('createdUtc',createdUtc).property('partitionKey',pk))",
            new Dictionary<string, object>
            {
                ["resId"] = resolutionId,
                ["summary"] = summary,
                ["snippet"] = snippet,
                ["createdUtc"] = nowUtc,
                ["pk"] = _settings.PartitionKey,
            }).ConfigureAwait(false);

        await client.SubmitAsync<dynamic>(
            "g.V(issueId).coalesce(outE('RESOLVED_BY').where(inV().hasId(resId))," +
            "addE('RESOLVED_BY').property('resolvedUtc',resolvedUtc).to(V(resId)))",
            new Dictionary<string, object>
            {
                ["issueId"] = messageId,
                ["resId"] = resolutionId,
                ["resolvedUtc"] = nowUtc,
            }).ConfigureAwait(false);

        await client.SubmitAsync<dynamic>(
            "g.V(issueId).property('status','resolved').property('resolvedUtc',resolvedUtc)",
            new Dictionary<string, object>
            {
                ["issueId"] = messageId,
                ["resolvedUtc"] = nowUtc,
            }).ConfigureAwait(false);

        _logger.LogInformation(
            "Recorded resolution for issue '{MessageId}'.",
            messageId);
    }

    public async ValueTask DisposeAsync()
    {
        if (_gremlinClient is not null)
        {
            _gremlinClient.Dispose();
            _gremlinClient = null;
        }
    }

    private async Task<GremlinClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (_gremlinClient is not null)
            return _gremlinClient;

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_gremlinClient is not null)
                return _gremlinClient;

            if (string.IsNullOrWhiteSpace(_settings.GremlinEndpoint) ||
                string.IsNullOrWhiteSpace(_settings.DatabaseName) ||
                string.IsNullOrWhiteSpace(_settings.GraphName) ||
                string.IsNullOrWhiteSpace(_settings.AccountKey))
            {
                throw new InvalidOperationException(
                    "GraphRag is enabled, but Gremlin configuration is incomplete.");
            }

            var uri = new Uri(_settings.GremlinEndpoint);
            var server = new GremlinServer(
                hostname: uri.Host,
                port: uri.Port,
                enableSsl: true,
                username: $"/dbs/{_settings.DatabaseName}/colls/{_settings.GraphName}",
                password: _settings.AccountKey);

            _gremlinClient = new GremlinClient(server);

            _logger.LogInformation(
                "Gremlin client connected to '{Endpoint}' (db: {Db}, graph: {Graph}).",
                uri.Host, _settings.DatabaseName, _settings.GraphName);

            return _gremlinClient;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static string StripCodeFence(string json)
    {
        if (!json.StartsWith("```", StringComparison.Ordinal))
            return json;

        string trimmed = json.Trim();
        trimmed = trimmed.Trim('`');
        if (trimmed.StartsWith("json", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[4..];
        return trimmed.Trim();
    }

    private static string? GetMapString(IDictionary<object, object> map, string key)
    {
        if (!map.TryGetValue(key, out object? value) || value is null)
            return null;

        if (value is IList<object> list)
        {
            if (list.Count == 0)
                return null;

            return list[0]?.ToString();
        }

        return value.ToString();
    }

    private static string NormalizeTopic(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length);
        bool lastDash = false;
        foreach (char c in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        return sb.ToString().Trim('-');
    }

    private static string GetDomain(string senderAddress)
    {
        int at = senderAddress.LastIndexOf('@');
        return at > -1 && at < senderAddress.Length - 1
            ? senderAddress[(at + 1)..]
            : "unknown";
    }

    private static GraphContext EmptyContext() =>
        new(
            OrganizationName: null,
            SupportTier: null,
            PriorEmailCount: 0,
            PriorIssues: [],
            KnownTopics: []);

    private static ExtractedEntities BuildFallbackEntities(EmailItem email)
    {
        string domain = GetDomain(email.SenderAddress);
        string normalized = NormalizeTopic(email.Subject);
        string topic = string.IsNullOrWhiteSpace(normalized) ? "general" : normalized;

        return new ExtractedEntities(
            SenderDomain: domain,
            OrganizationName: null,
            Topics: [topic],
            IssueSummary: email.Subject,
            PrimaryTopic: topic);
    }
}

#pragma warning restore OPENAI001
