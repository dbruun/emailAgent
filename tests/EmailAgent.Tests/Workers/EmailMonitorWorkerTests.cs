using Xunit;
using EmailAgent.Configuration;
using EmailAgent.Models;
using EmailAgent.Services;
using EmailAgent.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EmailAgent.Tests.Workers;

public class EmailMonitorWorkerTests
{
    private readonly IGraphEmailService _emailService = Substitute.For<IGraphEmailService>();
    private readonly IAIAgentService _aiService = Substitute.For<IAIAgentService>();
    private readonly IGraphRagService _graphRagService = Substitute.For<IGraphRagService>();

    private EmailMonitorWorker CreateWorker(
        int pollingSeconds = 30,
        string processedFolder = "Processed",
        bool graphRagEnabled = false) =>
        new(
            _emailService,
            _aiService,
            _graphRagService,
            Options.Create(new EmailProcessingSettings
            {
                PollingIntervalSeconds = pollingSeconds,
                ProcessedFolderName = processedFolder
            }),
            Options.Create(new GraphRagSettings { Enabled = graphRagEnabled }),
            NullLogger<EmailMonitorWorker>.Instance);

    private static EmailItem TestEmail(
        string id = "msg-1",
        string subject = "Help needed",
        string body = "I need assistance",
        string senderAddress = "alice@example.com",
        string senderName = "Alice") =>
        new(id, subject, body, senderAddress, senderName, DateTimeOffset.UtcNow);

    /// <summary>
    /// Starts the worker, waits for the ExecuteTask to finish (or timeout), then stops.
    /// The test mocks are expected to cancel <paramref name="cts"/> at the right time
    /// so the loop exits promptly.
    /// </summary>
    private static async Task RunOneCycleAsync(EmailMonitorWorker worker, CancellationTokenSource cts)
    {
        await worker.StartAsync(cts.Token);

        if (worker.ExecuteTask is { } task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
        }

        try { await worker.StopAsync(CancellationToken.None); }
        catch (OperationCanceledException) { }
    }

    // -----------------------------------------------------------------------
    // No emails
    // -----------------------------------------------------------------------

    [Fact]
    public async Task NoUnreadEmails_DoesNotCallAIService()
    {
        using var cts = new CancellationTokenSource();

        _emailService.GetUnreadEmailsAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();
                return Task.FromResult<IReadOnlyList<EmailItem>>([]);
            });

        var worker = CreateWorker();
        await RunOneCycleAsync(worker, cts);

        await _aiService.DidNotReceive().ProcessEmailAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<GraphContext?>(), Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // Happy path – single email
    // -----------------------------------------------------------------------

    [Fact]
    public async Task UnreadEmail_ProcessesAndRepliesAndMoves()
    {
        using var cts = new CancellationTokenSource();
        var email = TestEmail();

        _emailService.GetUnreadEmailsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EmailItem>>([email]));

        _aiService.ProcessEmailAsync(
                email.Subject, email.BodyText, email.SenderName, email.SenderAddress,
                null, Arg.Any<CancellationToken>())
            .Returns("AI reply");

        _emailService.MoveToFolderAsync(email.Id, "Processed", Arg.Any<CancellationToken>())
            .Returns(_ => { cts.Cancel(); return Task.CompletedTask; });

        var worker = CreateWorker(graphRagEnabled: false);
        await RunOneCycleAsync(worker, cts);

        await _aiService.Received(1).ProcessEmailAsync(
            email.Subject, email.BodyText, email.SenderName, email.SenderAddress,
            null, Arg.Any<CancellationToken>());
        await _emailService.Received(1).SendReplyAsync(
            email.Id, "AI reply", Arg.Any<CancellationToken>());
        await _emailService.Received(1).MoveToFolderAsync(
            email.Id, "Processed", Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // Empty reply from AI → skip reply & move
    // -----------------------------------------------------------------------

    [Fact]
    public async Task EmptyReply_SkipsReplyAndMove()
    {
        using var cts = new CancellationTokenSource();
        var email = TestEmail();

        _emailService.GetUnreadEmailsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EmailItem>>([email]));

        _aiService.ProcessEmailAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<GraphContext?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();
                return Task.FromResult(string.Empty);
            });

        var worker = CreateWorker(graphRagEnabled: false);
        await RunOneCycleAsync(worker, cts);

        await _emailService.DidNotReceive().SendReplyAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _emailService.DidNotReceive().MoveToFolderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhitespaceReply_SkipsReplyAndMove()
    {
        using var cts = new CancellationTokenSource();
        var email = TestEmail();

        _emailService.GetUnreadEmailsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EmailItem>>([email]));

        _aiService.ProcessEmailAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<GraphContext?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();
                return Task.FromResult("   ");
            });

        var worker = CreateWorker(graphRagEnabled: false);
        await RunOneCycleAsync(worker, cts);

        await _emailService.DidNotReceive().SendReplyAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // GraphRAG enabled – full pipeline
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GraphRagEnabled_FullPipeline()
    {
        using var cts = new CancellationTokenSource();
        var email = TestEmail();
        var entities = new ExtractedEntities("example.com", "Example Inc", ["billing"], "Billing issue", "billing");
        var graphCtx = new GraphContext("Example Inc", "premium", 5, [], ["billing"]);

        _emailService.GetUnreadEmailsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EmailItem>>([email]));

        _graphRagService.ExtractEntitiesAsync(email, Arg.Any<CancellationToken>())
            .Returns(entities);
        _graphRagService.GetGraphContextAsync(email.SenderAddress, Arg.Any<CancellationToken>())
            .Returns(graphCtx);

        _aiService.ProcessEmailAsync(
                email.Subject, email.BodyText, email.SenderName, email.SenderAddress,
                graphCtx, Arg.Any<CancellationToken>())
            .Returns("AI reply with context");

        _emailService.MoveToFolderAsync(email.Id, "Processed", Arg.Any<CancellationToken>())
            .Returns(_ => { cts.Cancel(); return Task.CompletedTask; });

        var worker = CreateWorker(graphRagEnabled: true);
        await RunOneCycleAsync(worker, cts);

        await _graphRagService.Received(1).ExtractEntitiesAsync(email, Arg.Any<CancellationToken>());
        await _graphRagService.Received(1).UpsertGraphAsync(email, entities, Arg.Any<CancellationToken>());
        await _graphRagService.Received(1).GetGraphContextAsync(email.SenderAddress, Arg.Any<CancellationToken>());
        await _graphRagService.Received(1).RecordResolutionAsync(
            email.Id, "AI reply with context", Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // GraphRAG extraction failure → continues with null context
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GraphRagExtractThrows_ContinuesWithNullContext()
    {
        using var cts = new CancellationTokenSource();
        var email = TestEmail();

        _emailService.GetUnreadEmailsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EmailItem>>([email]));

        _graphRagService.ExtractEntitiesAsync(email, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Gremlin down"));

        _aiService.ProcessEmailAsync(
                email.Subject, email.BodyText, email.SenderName, email.SenderAddress,
                null, Arg.Any<CancellationToken>())
            .Returns("Fallback reply");

        _emailService.MoveToFolderAsync(email.Id, "Processed", Arg.Any<CancellationToken>())
            .Returns(_ => { cts.Cancel(); return Task.CompletedTask; });

        var worker = CreateWorker(graphRagEnabled: true);
        await RunOneCycleAsync(worker, cts);

        // AI should have been called with null context (fallback)
        await _aiService.Received(1).ProcessEmailAsync(
            email.Subject, email.BodyText, email.SenderName, email.SenderAddress,
            null, Arg.Any<CancellationToken>());
        await _emailService.Received(1).SendReplyAsync(
            email.Id, "Fallback reply", Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // GraphRAG resolution failure → email still processed
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GraphRagResolutionThrows_EmailStillMoved()
    {
        using var cts = new CancellationTokenSource();
        var email = TestEmail();
        var entities = new ExtractedEntities("example.com", null, [], "Issue", "general");
        var graphCtx = new GraphContext(null, null, 0, [], []);

        _emailService.GetUnreadEmailsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EmailItem>>([email]));

        _graphRagService.ExtractEntitiesAsync(email, Arg.Any<CancellationToken>()).Returns(entities);
        _graphRagService.GetGraphContextAsync(email.SenderAddress, Arg.Any<CancellationToken>()).Returns(graphCtx);

        _aiService.ProcessEmailAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(),
                graphCtx, Arg.Any<CancellationToken>())
            .Returns("Reply");

        _graphRagService.RecordResolutionAsync(email.Id, "Reply", Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Resolution failed"));

        _emailService.MoveToFolderAsync(email.Id, "Processed", Arg.Any<CancellationToken>())
            .Returns(_ => { cts.Cancel(); return Task.CompletedTask; });

        var worker = CreateWorker(graphRagEnabled: true);
        await RunOneCycleAsync(worker, cts);

        await _emailService.Received(1).MoveToFolderAsync(
            email.Id, "Processed", Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // AI throws → error logged, no reply or move
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AIServiceThrows_DoesNotReplyOrMove()
    {
        using var cts = new CancellationTokenSource();
        var email = TestEmail();

        int getCallCount = 0;
        _emailService.GetUnreadEmailsAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                getCallCount++;
                if (getCallCount > 1) { cts.Cancel(); return Task.FromResult<IReadOnlyList<EmailItem>>([]); }
                return Task.FromResult<IReadOnlyList<EmailItem>>([email]);
            });

        _aiService.ProcessEmailAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<GraphContext?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("AI service unavailable"));

        var worker = CreateWorker(pollingSeconds: 0, graphRagEnabled: false);
        await RunOneCycleAsync(worker, cts);

        await _emailService.DidNotReceive().SendReplyAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _emailService.DidNotReceive().MoveToFolderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // Multiple emails – each processed
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MultipleEmails_ProcessesEach()
    {
        using var cts = new CancellationTokenSource();
        var email1 = TestEmail(id: "msg-1", subject: "First");
        var email2 = TestEmail(id: "msg-2", subject: "Second");

        _emailService.GetUnreadEmailsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EmailItem>>([email1, email2]));

        _aiService.ProcessEmailAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<GraphContext?>(), Arg.Any<CancellationToken>())
            .Returns("Reply");

        int moveCount = 0;
        _emailService.MoveToFolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (++moveCount >= 2) cts.Cancel();
                return Task.CompletedTask;
            });

        var worker = CreateWorker(graphRagEnabled: false);
        await RunOneCycleAsync(worker, cts);

        await _aiService.Received(2).ProcessEmailAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<GraphContext?>(), Arg.Any<CancellationToken>());
        await _emailService.Received(2).SendReplyAsync(
            Arg.Any<string>(), "Reply", Arg.Any<CancellationToken>());
        await _emailService.Received(2).MoveToFolderAsync(
            Arg.Any<string>(), "Processed", Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // Custom processed folder name
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CustomProcessedFolder_UsesConfiguredName()
    {
        using var cts = new CancellationTokenSource();
        var email = TestEmail();

        _emailService.GetUnreadEmailsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EmailItem>>([email]));

        _aiService.ProcessEmailAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<GraphContext?>(), Arg.Any<CancellationToken>())
            .Returns("Reply");

        _emailService.MoveToFolderAsync(email.Id, "Archive", Arg.Any<CancellationToken>())
            .Returns(_ => { cts.Cancel(); return Task.CompletedTask; });

        var worker = CreateWorker(processedFolder: "Archive", graphRagEnabled: false);
        await RunOneCycleAsync(worker, cts);

        await _emailService.Received(1).MoveToFolderAsync(
            email.Id, "Archive", Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // GraphRAG disabled → no graph calls at all
    // -----------------------------------------------------------------------

    [Fact]
    public async Task GraphRagDisabled_NoGraphCalls()
    {
        using var cts = new CancellationTokenSource();
        var email = TestEmail();

        _emailService.GetUnreadEmailsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EmailItem>>([email]));

        _aiService.ProcessEmailAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<GraphContext?>(), Arg.Any<CancellationToken>())
            .Returns("Reply");

        _emailService.MoveToFolderAsync(email.Id, "Processed", Arg.Any<CancellationToken>())
            .Returns(_ => { cts.Cancel(); return Task.CompletedTask; });

        var worker = CreateWorker(graphRagEnabled: false);
        await RunOneCycleAsync(worker, cts);

        await _graphRagService.DidNotReceive().ExtractEntitiesAsync(
            Arg.Any<EmailItem>(), Arg.Any<CancellationToken>());
        await _graphRagService.DidNotReceive().UpsertGraphAsync(
            Arg.Any<EmailItem>(), Arg.Any<ExtractedEntities>(), Arg.Any<CancellationToken>());
        await _graphRagService.DidNotReceive().GetGraphContextAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _graphRagService.DidNotReceive().RecordResolutionAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
