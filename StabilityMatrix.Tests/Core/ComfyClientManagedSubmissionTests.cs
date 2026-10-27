using StabilityMatrix.Tests;

using NSubstitute;

using Refit;
using StabilityMatrix.Core.Api;
using StabilityMatrix.Core.Inference;
using StabilityMatrix.Core.Models.Api.Comfy;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;

namespace StabilityMatrix.Tests.Core;

[TestClass]
public class ComfyClientManagedSubmissionTests
{
    private string? temporaryDirectory;

    [TestCleanup]
    public void CleanupTemporaryFiles()
    {
        if (temporaryDirectory is not null)
            TempFiles.DeleteDirectory(temporaryDirectory);
    }
    [TestMethod]
    public void WebSocketUri_PreservesBasePathAndMapsHttpsToWss()
    {
        var uri = ComfyClient.BuildWebSocketUri(new Uri("https://example.test/comfyui"), "client id");

        Assert.AreEqual("wss", uri.Scheme);
        Assert.AreEqual("/comfyui/ws", uri.AbsolutePath);
        Assert.AreEqual("client%20id", uri.Query.TrimStart('?').Split('=')[1]);
    }

    [TestMethod]
    public async Task RefitRequests_PreserveBasePathForComfyAndGraftRoutes()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler)
        {
            BaseAddress = ComfyClient.EnsureRefitBaseAddress(new Uri("https://example.test/comfyui/"))
        };
        var api = RestService.For<IComfyApi>(client);

        await api.PostPrompt(new ComfyPromptRequest
        {
            ClientId = "client",
            PromptId = "job-id",
            Prompt = new Dictionary<string, ComfyNode>()
        });
        await api.GetGraftJob("job-id");
        await api.CancelGraftJob("job-id");

        Assert.AreEqual("https://example.test/comfyui/prompt", handler.Requests[0].ToString());
        Assert.AreEqual("https://example.test/comfyui/graft/jobs/job-id", handler.Requests[1].ToString());
        Assert.AreEqual("https://example.test/comfyui/graft/jobs/job-id/cancel", handler.Requests[2].ToString());
    }

    [TestMethod]
    public async Task UncertainSubmission_PersistsIdentityAndDoesNotReplayAfterRestart()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var journalPath = Path.Combine(temporaryDirectory, "submissions.json");
        var apiFactory = Substitute.For<IApiFactory>();
        var api = Substitute.For<IComfyApi>();
        apiFactory.CreateRefitClient<IComfyApi>(Arg.Any<Uri>(), Arg.Any<RefitSettings>()).Returns(api);
        api.PostPrompt(Arg.Any<ComfyPromptRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<ComfyPromptRequest>();
                return new ComfyPromptResponse
                {
                    PromptId = request.PromptId!,
                    Number = 0,
                    NodeErrors = new Dictionary<string, object?>(),
                    Graft = new ComfyGraftPromptInfo { State = "uncertain" }
                };
            });
        api.GetGraftJob(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = call.ArgAt<string>(0);
                return new ComfyGraftJob
                {
                    JobId = id,
                    State = "uncertain",
                    ActiveAttempt = new ComfyGraftAttempt { State = "uncertain" }
                };
            });

        string originalClientId;
        string originalPromptId;
        using (var first = new ComfyClient(apiFactory, new Uri("http://example.test/comfyui/"), journalPath))
        {
            originalClientId = first.ClientId;
            var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                first.QueuePromptAsync(new Dictionary<string, ComfyNode>())
            );
            originalPromptId = api.ReceivedCalls()
                .SelectMany(call => call.GetArguments())
                .OfType<ComfyPromptRequest>()
                .Single()
                .PromptId!;
            StringAssert.Contains(error.Message, originalPromptId);
        }

        using (var restarted = new ComfyClient(apiFactory, new Uri("http://example.test/comfyui/"), journalPath))
        {
            Assert.AreEqual(originalClientId, restarted.ClientId);
            var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                restarted.QueuePromptAsync(new Dictionary<string, ComfyNode>())
            );
            StringAssert.Contains(error.Message, originalPromptId);
            await api.Received(1).PostPrompt(Arg.Any<ComfyPromptRequest>(), Arg.Any<CancellationToken>());
        }
    }

[DataTestMethod]
[DataRow("accepted")]
[DataRow("running")]
public async Task QueuePromptAsync_RefusesNewGraphForActiveJobAfterRestart(string activeState)
{
    temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    var journalPath = Path.Combine(temporaryDirectory, "submissions.json");
    var factory = Substitute.For<IApiFactory>();
    var api = Substitute.For<IComfyApi>();
    factory.CreateRefitClient<IComfyApi>(Arg.Any<Uri>(), Arg.Any<RefitSettings>()).Returns(api);
    var postedRequests = new List<ComfyPromptRequest>();
    api.PostPrompt(Arg.Any<ComfyPromptRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
    {
        var request = call.Arg<ComfyPromptRequest>();
        postedRequests.Add(request);
        return new ComfyPromptResponse
        {
            PromptId = request.PromptId!,
            Number = 0,
            NodeErrors = new(),
            Graft = new ComfyGraftPromptInfo { State = "accepted" }
        };
    });
    api.GetGraftJob(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
    {
        var id = call.ArgAt<string>(0);
        return new ComfyGraftJob
        {
            JobId = id,
            State = activeState,
            ActiveAttempt = new ComfyGraftAttempt { State = activeState }
        };
    });

    string originalJobId;
    string originalClientId;
    var originalGraph = new Dictionary<string, ComfyNode>
    {
        ["original"] = new() { ClassType = "OriginalNode", Inputs = new() }
    };
    using (var firstTab = new ComfyClient(factory, new Uri("http://example.test/comfyui/"), journalPath))
    {
        originalClientId = firstTab.ClientId;
        var originalTask = await firstTab.QueuePromptAsync(originalGraph);
        originalJobId = originalTask.Id;
    }

    using var secondTab = new ComfyClient(factory, new Uri("http://example.test/comfyui/"), journalPath);
    var changedGraph = new Dictionary<string, ComfyNode>
    {
        ["changed"] = new() { ClassType = "ChangedNode", Inputs = new() }
    };
    var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
        secondTab.QueuePromptAsync(changedGraph));

    StringAssert.Contains(error.Message, originalJobId);
    StringAssert.Contains(error.Message, activeState);
    StringAssert.Contains(error.Message, "not submitted");
    using (var journalAfterRestart = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(journalPath)))
    {
        var record = journalAfterRestart.RootElement;
        Assert.AreEqual(originalClientId, record.GetProperty("client_id").GetString());
        Assert.AreEqual(originalJobId, record.GetProperty("prompt_id").GetString());
        Assert.AreEqual(activeState, record.GetProperty("state").GetString());
    }
    using (var reloadedTab = new ComfyClient(factory, new Uri("http://example.test/comfyui/"), journalPath))
    {
        var reloadedError = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            reloadedTab.QueuePromptAsync(changedGraph));
        StringAssert.Contains(reloadedError.Message, originalJobId);
        StringAssert.Contains(reloadedError.Message, activeState);
    }
    Assert.AreEqual(1, postedRequests.Count);
    Assert.AreEqual(originalJobId, postedRequests[0].PromptId);
    Assert.IsTrue(postedRequests[0].Prompt.ContainsKey("original"));
    Assert.IsFalse(postedRequests[0].Prompt.ContainsKey("changed"));
}

[TestMethod]
public async Task ReconcileManagedSubmissionAsync_RemainsAvailableForActiveJob()
{
    temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    var factory = Substitute.For<IApiFactory>();
    var api = Substitute.For<IComfyApi>();
    factory.CreateRefitClient<IComfyApi>(Arg.Any<Uri>(), Arg.Any<RefitSettings>()).Returns(api);
    api.PostPrompt(Arg.Any<ComfyPromptRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        new ComfyPromptResponse
        {
            PromptId = call.Arg<ComfyPromptRequest>().PromptId!,
            Number = 0,
            NodeErrors = new(),
            Graft = new ComfyGraftPromptInfo { State = "accepted" }
        });
    api.GetGraftJob(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
    {
        var id = call.ArgAt<string>(0);
        return new ComfyGraftJob
        {
            JobId = id,
            State = "running",
            ActiveAttempt = new ComfyGraftAttempt { State = "running" }
        };
    });
    using var client = new ComfyClient(factory, new Uri("http://example.test/comfyui/"),
        Path.Combine(temporaryDirectory, "submissions.json"));

    var task = await client.QueuePromptAsync(new Dictionary<string, ComfyNode>());
    var reconciled = await client.ReconcileManagedSubmissionAsync();

    Assert.AreEqual(task.Id, reconciled!.JobId);
    Assert.AreEqual("running", reconciled.EffectiveState);
    await api.Received(1).PostPrompt(Arg.Any<ComfyPromptRequest>(), Arg.Any<CancellationToken>());
}

    [TestMethod]
    public async Task ManagedCancellation_UsesTheDurableJobId()
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var apiFactory = Substitute.For<IApiFactory>();
        var api = Substitute.For<IComfyApi>();
        apiFactory.CreateRefitClient<IComfyApi>(Arg.Any<Uri>(), Arg.Any<RefitSettings>()).Returns(api);
        api.PostPrompt(Arg.Any<ComfyPromptRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = call.Arg<ComfyPromptRequest>().PromptId!;
                return new ComfyPromptResponse
                {
                    PromptId = id,
                    Number = 0,
                    NodeErrors = new Dictionary<string, object?>(),
                    Graft = new ComfyGraftPromptInfo { State = "accepted" }
                };
            });
        api.GetGraftJob(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = call.ArgAt<string>(0);
                return new ComfyGraftJob
                {
                    JobId = id,
                    State = "accepted",
                    ActiveAttempt = new ComfyGraftAttempt { State = "accepted" }
                };
            });
        api.CancelGraftJob(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ComfyGraftCancelResponse { Cancelled = true });

        using var client = new ComfyClient(
            apiFactory,
            new Uri("http://example.test/comfyui/"),
            Path.Combine(temporaryDirectory, "submissions.json")
        );
        var task = await client.QueuePromptAsync(new Dictionary<string, ComfyNode>());
        await client.InterruptPromptAsync();

        await api.Received(1).CancelGraftJob(task.Id, Arg.Any<CancellationToken>());
    }

    [DataTestMethod]
    [DataRow("completed", TaskStatus.RanToCompletion)]
    [DataRow("failed", TaskStatus.Faulted)]
    [DataRow("cancelled", TaskStatus.Canceled)]
    public async Task Reconciliation_SettlesTaskWhenTerminalEventWasLost(string state, TaskStatus expected)
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var factory = Substitute.For<IApiFactory>();
        var api = Substitute.For<IComfyApi>();
        factory.CreateRefitClient<IComfyApi>(Arg.Any<Uri>(), Arg.Any<RefitSettings>()).Returns(api);
        var observedState = "accepted";
        api.PostPrompt(Arg.Any<ComfyPromptRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
            new ComfyPromptResponse {
                PromptId = call.Arg<ComfyPromptRequest>().PromptId!, Number = 0, NodeErrors = new(),
                Graft = new ComfyGraftPromptInfo { State = "accepted" }
            });
        api.GetGraftJob(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            new ComfyGraftJob {
                JobId = call.ArgAt<string>(0), State = observedState,
                ActiveAttempt = new ComfyGraftAttempt { State = observedState }
            });
        using var client = new ComfyClient(factory, new Uri("http://example.test/comfyui/"),
            Path.Combine(temporaryDirectory, "submissions.json"));
        var task = await client.QueuePromptAsync(new Dictionary<string, ComfyNode>());
        Assert.IsFalse(task.Task.IsCompleted);
        observedState = state;
        await client.ReconcileManagedSubmissionAsync();
        Assert.AreEqual(expected, task.Task.Status);
        Assert.IsFalse(client.PromptTasks.ContainsKey(task.Id));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RejectedSubmission_OnlyExplicitMatchingRejectionPermitsNewWork(bool explicitRejection)
    {
        temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var journal = Path.Combine(temporaryDirectory, "submissions.json");
        using var handler = new RejectionHandler(explicitRejection);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://example.test/comfyui") };
        var api = RestService.For<IComfyApi>(http);
        var factory = Substitute.For<IApiFactory>();
        factory.CreateRefitClient<IComfyApi>(Arg.Any<Uri>(), Arg.Any<RefitSettings>()).Returns(api);
        using (var first = new ComfyClient(factory, new Uri("http://example.test/comfyui/"), journal))
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                first.QueuePromptAsync(new Dictionary<string, ComfyNode>()));
        using var restarted = new ComfyClient(factory, new Uri("http://example.test/comfyui/"), journal);
        if (explicitRejection)
        {
            var corrected = await restarted.QueuePromptAsync(new Dictionary<string, ComfyNode>());
            Assert.AreEqual(TaskStatus.RanToCompletion, corrected.Task.Status);
            Assert.AreEqual(2, handler.PromptIds.Count);
            Assert.AreNotEqual(handler.PromptIds[0], handler.PromptIds[1]);
        }
        else
        {
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                restarted.QueuePromptAsync(new Dictionary<string, ComfyNode>()));
            Assert.AreEqual(1, handler.PromptIds.Count);
        }
    }

    private sealed class RejectionHandler(bool explicitRejection) : HttpMessageHandler
    {
        public List<string> PromptIds { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post)
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { RequestMessage = request };
            using var body = System.Text.Json.JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            var id = body.RootElement.GetProperty("prompt_id").GetString()!;
            PromptIds.Add(id);
            var rejected = PromptIds.Count == 1;
            var response = rejected
                ? explicitRejection
                    ? $"{{\"prompt_id\":\"{id}\",\"graft\":{{\"state\":\"rejected\"}}}}"
                    : "{\"error\":\"unknown intermediary rejection\"}"
                : $"{{\"prompt_id\":\"{id}\",\"number\":0,\"node_errors\":{{}},\"graft\":{{\"state\":\"completed\"}}}}";
            return new HttpResponseMessage(rejected
                ? System.Net.HttpStatusCode.UnprocessableEntity : System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(
                new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"prompt_id\":\"job-id\",\"number\":0,\"node_errors\":{},\"job_id\":\"job-id\",\"state\":\"uncertain\",\"cancelled\":true}",
                        System.Text.Encoding.UTF8,
                        "application/json"
                    )
                }
            );
        }
    }
}
