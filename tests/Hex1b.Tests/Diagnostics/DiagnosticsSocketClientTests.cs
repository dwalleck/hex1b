using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hex1b.Diagnostics;

namespace Hex1b.Tests.Diagnostics;

/// <summary>
/// The shared socket client turns transport and protocol failures into the same diagnostic
/// outcomes for every client, instead of empty content or client-specific errors.
/// </summary>
[TestClass]
public class DiagnosticsSocketClientTests
{
    [TestMethod]
    public async Task Capture_NoListener_IsUnavailableTargetUnreachable()
    {
        var path = TempSocketPath();

        var result = await new DiagnosticsSocketClient().CaptureAsync(path, new DiagnosticCaptureRequest(),
            TestContext.Current.CancellationToken);

        Assert.AreEqual(DiagnosticOutcome.Unavailable, result.Outcome);
        Assert.AreEqual("target-unreachable", result.Problem!.Code);
        Assert.IsNull(result.Content);
        Assert.IsNull(result.Identity);
    }

    [TestMethod]
    public async Task Capture_MalformedResponse_IsFailedProtocolError()
    {
        await using var server = await FakeServer.StartAsync(_ => "this is not json");

        var result = await new DiagnosticsSocketClient().CaptureAsync(server.Path, new DiagnosticCaptureRequest(),
            TestContext.Current.CancellationToken);

        Assert.AreEqual(DiagnosticOutcome.Failed, result.Outcome);
        Assert.AreEqual("protocol-error", result.Problem!.Code);
    }

    [TestMethod]
    public async Task Capture_LegacyTargetWithoutContract_IsIncompatibleNotEmptySuccess()
    {
        // An older target answers "capture" with only a data string and no contract result.
        await using var server = await FakeServer.StartAsync(_ => """{"success":true,"data":"plain","width":80,"height":24}""");

        var result = await new DiagnosticsSocketClient().CaptureAsync(server.Path, new DiagnosticCaptureRequest(),
            TestContext.Current.CancellationToken);

        Assert.AreEqual(DiagnosticOutcome.Failed, result.Outcome);
        Assert.AreEqual("incompatible-target", result.Problem!.Code);
        Assert.IsNull(result.Content, "legacy plain text must not be presented as a contract capture");
    }

    [TestMethod]
    public async Task Capture_NoResponseWithinTimeout_IsFailedTimeout()
    {
        await using var server = await FakeServer.StartAsync(_ => null);

        var result = await new DiagnosticsSocketClient(TimeSpan.FromMilliseconds(200)).CaptureAsync(
            server.Path, new DiagnosticCaptureRequest(), TestContext.Current.CancellationToken);

        Assert.AreEqual(DiagnosticOutcome.Failed, result.Outcome);
        Assert.AreEqual("timeout", result.Problem!.Code);
    }

    [TestMethod]
    public async Task Capture_SendsTheSharedRequestShape()
    {
        string? received = null;
        await using var server = await FakeServer.StartAsync(line =>
        {
            received = line;
            return """{"success":false,"error":"x","capture":{"contractVersion":1,"outcome":"invalid-request","problem":{"code":"c","message":"m"}}}""";
        });

        var result = await new DiagnosticsSocketClient().CaptureAsync(server.Path, new DiagnosticCaptureRequest
        {
            Format = DiagnosticCaptureFormat.Html,
            HistoryRows = 7,
            Authorizations = [DiagnosticAuthorization.NonScreenMetadata]
        }, TestContext.Current.CancellationToken);

        StringAssert.Contains(received, "\"method\":\"capture\"");
        StringAssert.Contains(received, "\"capture\":{\"format\":\"html\",\"historyRows\":7,\"authorizations\":[\"non-screen-metadata\"]}");
        Assert.AreEqual(DiagnosticOutcome.InvalidRequest, result.Outcome, $"the target's own outcome must be preserved: {result.Problem?.Code} {result.Problem?.Message}");
        Assert.AreEqual("c", result.Problem!.Code);
    }

    [TestMethod]
    public async Task Capture_TargetWithoutSequenceOrSyncState_ExplainsInsteadOfFabricating()
    {
        // A contract-1 target built before model sequences existed: a complete capture with neither field.
        await using var server = await FakeServer.StartAsync(_ =>
            """
            {"success":true,"capture":{"contractVersion":1,"outcome":"captured","format":"text","content":"x",
            "geometry":{"columns":1,"rows":1},"history":{"requestedRows":0,"returnedRows":0},
            "identity":{"processId":1,"sessionId":"s","sourceLayer":"terminal-model"}}}
            """.ReplaceLineEndings(""));

        var result = await new DiagnosticsSocketClient().CaptureAsync(server.Path, new DiagnosticCaptureRequest(),
            TestContext.Current.CancellationToken);

        Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome);
        var identity = JsonSerializer.SerializeToElement(result, DiagnosticsJsonContext.Default.DiagnosticCaptureResult)
            .GetProperty("identity");
        Assert.IsFalse(identity.TryGetProperty("modelSequence", out var fabricated),
            $"the client reported modelSequence {fabricated} for a target that sent none");
        foreach (var field in new[] { "identity.modelSequence", "synchronizedUpdate" })
            Assert.IsTrue(result.UnavailableFields.Any(f => f.Field == field && f.Reason.Length > 0),
                $"{field} is absent without an explanation");
    }

    [TestMethod]
    public async Task Capture_TargetExplainingItsAbsentSequence_IsNotExplainedTwice()
    {
        await using var server = await FakeServer.StartAsync(_ =>
            """
            {"success":true,"capture":{"contractVersion":1,"outcome":"captured","format":"text","content":"x",
            "geometry":{"columns":1,"rows":1},"history":{"requestedRows":0,"returnedRows":0},
            "identity":{"processId":1,"sessionId":"s","sourceLayer":"terminal-model"},
            "synchronizedUpdate":{"active":false},
            "unavailableFields":[{"field":"identity.modelSequence","reason":"target reason"}]}}
            """.ReplaceLineEndings(""));

        var result = await new DiagnosticsSocketClient().CaptureAsync(server.Path, new DiagnosticCaptureRequest(),
            TestContext.Current.CancellationToken);

        Assert.AreEqual("target reason", result.UnavailableFields.Single(f => f.Field == "identity.modelSequence").Reason);
        Assert.IsFalse(result.UnavailableFields.Any(f => f.Field == "synchronizedUpdate"), "a reported field was marked unavailable");
    }

    [TestMethod]
    public async Task Capture_MalformedNullListEntries_AreDroppedNotThrown()
    {
        await using var server = await FakeServer.StartAsync(_ =>
            """
            {"success":true,"capture":{"contractVersion":1,"outcome":"captured","format":"text","content":"x",
            "geometry":{"columns":1,"rows":1},"history":{"requestedRows":0,"returnedRows":0},
            "identity":{"processId":1,"sessionId":"s","sourceLayer":"terminal-model","modelSequence":3},
            "synchronizedUpdate":{"active":false},
            "unavailableFields":[null],"contentCoverage":[null],"limitations":[null]}}
            """.ReplaceLineEndings(""));

        var result = await new DiagnosticsSocketClient().CaptureAsync(server.Path, new DiagnosticCaptureRequest(),
            TestContext.Current.CancellationToken);

        Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome);
        Assert.IsFalse(result.UnavailableFields.Any(f => f is null) || result.ContentCoverage.Any(c => c is null) ||
            result.Limitations.Any(l => l is null), "null list entries reached the caller");
    }

    [TestMethod]
    public async Task Capture_NullEntriesWithOmittedFields_DoNotThrow()
    {
        // The explanation step reads unavailableFields entries when a field is omitted.
        await using var server = await FakeServer.StartAsync(_ =>
            """
            {"success":true,"capture":{"contractVersion":1,"outcome":"captured","format":"text","content":"x",
            "geometry":{"columns":1,"rows":1},"history":{"requestedRows":0,"returnedRows":0},
            "identity":{"processId":1,"sessionId":"s","sourceLayer":"terminal-model"},
            "unavailableFields":[null]}}
            """.ReplaceLineEndings(""));

        var result = await new DiagnosticsSocketClient().CaptureAsync(server.Path, new DiagnosticCaptureRequest(),
            TestContext.Current.CancellationToken);

        Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome);
        Assert.IsTrue(result.UnavailableFields.Any(f => f.Field == "identity.modelSequence"));
    }

    [TestMethod]
    public void DescribePartialContent_WithoutStartSequence_DoesNotRenderAnEmptyValue()
    {
        var note = TerminalDiagnostics.DescribePartialContent(new DiagnosticCaptureResult
        {
            Outcome = DiagnosticOutcome.Captured,
            SynchronizedUpdate = new DiagnosticSynchronizedUpdate { Active = true },
        });

        StringAssert.Contains(note, "partially applied");
        Assert.IsFalse(note!.Contains("sequence ;", StringComparison.Ordinal), $"rendered an absent start sequence: {note}");
    }

    [TestMethod]
    public async Task Capabilities_LegacyTarget_IsIncompatible()
    {
        await using var server = await FakeServer.StartAsync(_ => """{"success":false,"error":"Unknown method: capabilities"}""");

        var capabilities = await new DiagnosticsSocketClient().GetCapabilitiesAsync(server.Path, TestContext.Current.CancellationToken);

        Assert.AreEqual(DiagnosticOutcome.Failed, capabilities.Outcome);
        Assert.AreEqual("incompatible-target", capabilities.Problem!.Code);
        StringAssert.Contains(capabilities.Problem.Message, "Unknown method");
        Assert.AreEqual(0, capabilities.Operations.Count);
    }

    [TestMethod]
    public async Task Send_NoResponseWithinTimeout_ThrowsTimeoutNotCancellation()
    {
        await using var server = await FakeServer.StartAsync(_ => null);

        var error = await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            new DiagnosticsSocketClient().SendAsync(
                server.Path, new DiagnosticsRequest { Method = "info" }, TestContext.Current.CancellationToken,
                TimeSpan.FromMilliseconds(200)));

        StringAssert.Contains(error.Message, "did not respond");
    }

    [TestMethod]
    public async Task Send_WithoutTimeout_WaitsForASlowTargetInsteadOfReportingFailure()
    {
        // Non-idempotent requests must not be reported failed while the target is still applying them.
        await using var server = await FakeServer.StartAsync(_ =>
        {
            Thread.Sleep(400);
            return """{"success":true,"data":"Sent 3 bytes"}""";
        });

        var response = await new DiagnosticsSocketClient(TimeSpan.FromMilliseconds(100)).SendAsync(
            server.Path, new DiagnosticsRequest { Method = "input", Data = "abc" }, TestContext.Current.CancellationToken);

        Assert.IsTrue(response.Success);
    }

    [TestMethod]
    public async Task Capture_ResultWithoutContractVersion_IsIncompatible()
    {
        await using var server = await FakeServer.StartAsync(_ =>
            """{"success":false,"capture":{"outcome":"invalid-request","problem":{"code":"c","message":"m"}}}""");

        var result = await new DiagnosticsSocketClient().CaptureAsync(server.Path, new DiagnosticCaptureRequest(),
            TestContext.Current.CancellationToken);

        Assert.AreEqual("incompatible-target", result.Problem!.Code);
    }

    [TestMethod]
    public async Task Capture_OtherContractVersion_IsIncompatible()
    {
        await using var server = await FakeServer.StartAsync(_ =>
            """{"success":true,"capture":{"contractVersion":2,"outcome":"captured","format":"text","content":"x"}}""");

        var result = await new DiagnosticsSocketClient().CaptureAsync(server.Path, new DiagnosticCaptureRequest(),
            TestContext.Current.CancellationToken);

        Assert.AreEqual(DiagnosticOutcome.Failed, result.Outcome);
        Assert.AreEqual("incompatible-target", result.Problem!.Code);
        StringAssert.Contains(result.Problem.Message, "version 2");
    }

    [TestMethod]
    public async Task Capture_CapturedWithoutRequiredFields_IsProtocolError()
    {
        await using var server = await FakeServer.StartAsync(_ =>
            """{"success":true,"capture":{"contractVersion":1,"outcome":"captured","format":"text","content":"x"}}""");

        var result = await new DiagnosticsSocketClient().CaptureAsync(server.Path, new DiagnosticCaptureRequest(),
            TestContext.Current.CancellationToken);

        Assert.AreEqual(DiagnosticOutcome.Failed, result.Outcome);
        Assert.AreEqual("protocol-error", result.Problem!.Code);
    }

    [TestMethod]
    public async Task Capture_InvalidEndpoint_IsTransportFailureNotAnException()
    {
        var tooLong = Path.Combine(Path.GetTempPath(), new string('p', 400) + ".socket");

        var result = await new DiagnosticsSocketClient().CaptureAsync(tooLong, new DiagnosticCaptureRequest(),
            TestContext.Current.CancellationToken);

        Assert.AreNotEqual(DiagnosticOutcome.Captured, result.Outcome);
        Assert.IsNotNull(result.Problem);
    }

    [TestMethod]
    public async Task Capture_CallerCancellation_Throws()
    {
        await using var server = await FakeServer.StartAsync(_ => null);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new DiagnosticsSocketClient().CaptureAsync(server.Path, new DiagnosticCaptureRequest(), cts.Token));
    }

    [TestMethod]
    public async Task ApplicationFrame_CapturedFrameWithMissingNestedLists_IsProtocolError()
    {
        var complete = new DiagnosticApplicationFrameResult
        {
            Outcome = DiagnosticOutcome.Captured,
            Identity = new DiagnosticObservationIdentity(),
            Frame = new DiagnosticApplicationFrame
            {
                Root = new DiagnosticFrameNode { Type = "Root", Children = [new DiagnosticFrameNode { Type = "Leaf" }] },
            },
        };
        var json = System.Text.Json.Nodes.JsonNode.Parse(
            System.Text.Json.JsonSerializer.Serialize(complete, DiagnosticsJsonContext.Default.DiagnosticApplicationFrameResult))!;
        json["frame"]!["root"]!["children"]![0]!.AsObject().Remove("children");
        var response = $$"""{"success":true,"applicationFrame":{{json.ToJsonString()}}}""";
        await using var server = await FakeServer.StartAsync(_ => response);
        await using var valid = await FakeServer.StartAsync(_ =>
            $$"""{"success":true,"applicationFrame":{{System.Text.Json.JsonSerializer.Serialize(complete, DiagnosticsJsonContext.Default.DiagnosticApplicationFrameResult)}}}""");

        var result = await new DiagnosticsSocketClient().CaptureApplicationFrameAsync(server.Path,
            new DiagnosticApplicationFrameRequest(), TestContext.Current.CancellationToken);
        var accepted = await new DiagnosticsSocketClient().CaptureApplicationFrameAsync(valid.Path,
            new DiagnosticApplicationFrameRequest(), TestContext.Current.CancellationToken);

        Assert.AreEqual((DiagnosticOutcome.Failed, "protocol-error"), (result.Outcome, result.Problem?.Code));
        Assert.AreEqual(DiagnosticOutcome.Captured, accepted.Outcome, accepted.Problem?.Message);
    }

    [TestMethod]
    [DataRow("""{"popups":[null],"focus":{"focusables":[]}}""")]
    [DataRow("""{"popups":[],"focus":{"focusables":[null]}}""")]
    [DataRow("""{"popups":[],"focus":null}""")]
    [DataRow("""{"popups":[],"focus":{"focusables":[]},"focusedEditor":{"bounds":{},"carets":[],"selections":[null]}}""")]
    [DataRow("""{"popups":[],"focus":{"focusables":[]},"focusedEditor":{"bounds":{},"carets":[],"selections":[{"start":null,"end":{}}]}}""")]
    [DataRow("""{"popups":[],"focus":{"focusables":[]},"root":{"type":"x","hitTestBounds":{},"contentBounds":{},"visibleBounds":{},"children":[]}}""")]
    [DataRow("""{"popups":[],"focus":{"focusables":[]},"root":{"type":"x","bounds":{},"hitTestBounds":{},"contentBounds":{},"visibleBounds":{},"children":[null]}}""")]
    public async Task ApplicationFrame_MalformedCapturedFrame_IsProtocolError(string frame)
    {
        var response = """{"success":true,"applicationFrame":{"contractVersion":1,"outcome":"captured","identity":{},"frame":""" + frame + "}}";
        await using var server = await FakeServer.StartAsync(_ => response);

        var result = await new DiagnosticsSocketClient().CaptureApplicationFrameAsync(server.Path,
            new DiagnosticApplicationFrameRequest(), TestContext.Current.CancellationToken);

        Assert.AreEqual((DiagnosticOutcome.Failed, "protocol-error"), (result.Outcome, result.Problem?.Code), frame);
    }

    [TestMethod]
    public async Task ApplicationFrame_MalformedEntriesAndStrayFrames_NeverThrow()
    {
        var withNullCaret = """{"success":true,"applicationFrame":{"contractVersion":1,"outcome":"captured","identity":{},"frame":{"popups":[],"focus":{"focusables":[]},"focusedEditor":{"bounds":{},"carets":[null],"selections":[]}}}}""";
        var strayFrame = """{"success":false,"applicationFrame":{"contractVersion":1,"outcome":"unavailable","problem":{"code":"x","message":"y"},"frame":{"focus":null}}}""";
        await using var nullCaret = await FakeServer.StartAsync(_ => withNullCaret);
        await using var stray = await FakeServer.StartAsync(_ => strayFrame);

        var malformed = await new DiagnosticsSocketClient().CaptureApplicationFrameAsync(nullCaret.Path,
            new DiagnosticApplicationFrameRequest(), TestContext.Current.CancellationToken);
        var unavailable = await new DiagnosticsSocketClient().CaptureApplicationFrameAsync(stray.Path,
            new DiagnosticApplicationFrameRequest(), TestContext.Current.CancellationToken);

        Assert.AreEqual((DiagnosticOutcome.Failed, "protocol-error"), (malformed.Outcome, malformed.Problem?.Code));
        Assert.AreEqual(DiagnosticOutcome.Unavailable, unavailable.Outcome);
        Assert.IsNull(unavailable.Frame, "a frame on a non-captured outcome reached the caller");
    }

    [TestMethod]
    public async Task Milestone_ExchangeLastsTheMilestoneTimeoutBeyondTheObservationLimit()
    {
        // The target answers after 600 ms: past a 200 ms observation limit, within 200 ms + 1,000 ms.
        var answer = """{"success":false,"capture":{"contractVersion":1,"outcome":"timed-out","problem":{"code":"milestone-timed-out","message":"late"}}}""";
        await using var server = await FakeServer.StartAsync(_ =>
        {
            Thread.Sleep(600);
            return answer;
        });
        var client = new DiagnosticsSocketClient(TimeSpan.FromMilliseconds(200));

        var withMilestone = await client.CaptureAsync(server.Path, new DiagnosticCaptureRequest
        {
            Milestone = new DiagnosticMilestoneRequest { Milestone = DiagnosticMilestone.InputProcessed, InputId = 1, TimeoutMs = 1000 },
        }, TestContext.Current.CancellationToken);
        var immediate = await client.CaptureAsync(server.Path, new DiagnosticCaptureRequest(), TestContext.Current.CancellationToken);

        Assert.AreEqual((DiagnosticOutcome.TimedOut, "milestone-timed-out"), (withMilestone.Outcome, withMilestone.Problem?.Code),
            "the client cut off a milestone wait at its observation limit");
        Assert.AreEqual((DiagnosticOutcome.Failed, "timeout"), (immediate.Outcome, immediate.Problem?.Code),
            "fixture: without a milestone the observation limit applies");
    }

    [TestMethod]
    public async Task Milestone_TargetIgnoringTheMilestone_IsIncompatibleNotCaptured()
    {
        // A target built before milestones: it drops the unknown field and captures at once.
        await using var server = await FakeServer.StartAsync(line => line.Contains("\"method\":\"application-frame\"", StringComparison.Ordinal)
            ? """{"success":true,"applicationFrame":{"contractVersion":1,"outcome":"captured","identity":{},"frame":{"popups":[],"focus":{"focusables":[]}}}}"""
            : """
              {"success":true,"capture":{"contractVersion":1,"outcome":"captured","format":"text","content":"x",
              "geometry":{"columns":1,"rows":1},"history":{"requestedRows":0,"returnedRows":0},
              "identity":{"processId":1,"sessionId":"s","sourceLayer":"terminal-model"}}}
              """.ReplaceLineEndings(""));
        var milestone = new DiagnosticMilestoneRequest { Milestone = DiagnosticMilestone.InputProcessed, InputId = 1 };
        var client = new DiagnosticsSocketClient();

        var capture = await client.CaptureAsync(server.Path, new DiagnosticCaptureRequest { Milestone = milestone }, TestContext.Current.CancellationToken);
        var frame = await client.CaptureApplicationFrameAsync(server.Path, new DiagnosticApplicationFrameRequest { Milestone = milestone },
            TestContext.Current.CancellationToken);
        var plain = await client.CaptureAsync(server.Path, new DiagnosticCaptureRequest(), TestContext.Current.CancellationToken);

        Assert.AreEqual((DiagnosticOutcome.Failed, "incompatible-target"), (capture.Outcome, capture.Problem?.Code),
            "a capture that ignored the milestone was reported as captured");
        Assert.AreEqual((DiagnosticOutcome.Failed, "incompatible-target"), (frame.Outcome, frame.Problem?.Code));
        Assert.AreEqual(DiagnosticOutcome.Captured, plain.Outcome, "fixture: the target captures without a milestone");
    }

    [TestMethod]
    public async Task ApplicationFrame_TargetWithoutTheMethod_IsIncompatible()
    {
        await using var server = await FakeServer.StartAsync(_ => """{"success":false,"error":"Unknown method: application-frame"}""");

        var result = await new DiagnosticsSocketClient().CaptureApplicationFrameAsync(server.Path,
            new DiagnosticApplicationFrameRequest(), TestContext.Current.CancellationToken);

        Assert.AreEqual((DiagnosticOutcome.Failed, "incompatible-target"), (result.Outcome, result.Problem?.Code));
    }

    private static string TempSocketPath() =>
        Path.Combine(Path.GetTempPath(), $"hex1b-{Guid.NewGuid():N}.socket");

    // A minimal real Unix socket peer that answers one line per connection.
    private sealed class FakeServer : IAsyncDisposable
    {
        private readonly Socket _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        private FakeServer(string path, Func<string, string?> respond)
        {
            Path = path;
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(path));
            _listener.Listen(4);
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    Socket client;
                    try { client = await _listener.AcceptAsync(_cts.Token); }
                    catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using var _ = client;
                        await using var stream = new NetworkStream(client, ownsSocket: false);
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        var line = await reader.ReadLineAsync();
                        var response = respond(line ?? "");
                        if (response is null)
                        {
                            try { await Task.Delay(Timeout.Infinite, _cts.Token); } catch { }
                            return;
                        }
                        var bytes = Encoding.UTF8.GetBytes(response + "\n");
                        await stream.WriteAsync(bytes);
                    });
                }
            });
        }

        public string Path { get; }

        public static Task<FakeServer> StartAsync(Func<string, string?> respond) =>
            Task.FromResult(new FakeServer(TempSocketPath(), respond));

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Dispose();
            try { await _loop; } catch { }
            File.Delete(Path);
            _cts.Dispose();
        }
    }
}
