using System.Net.Sockets;
using System.Text;
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
            return """{"success":false,"error":"x","capture":{"outcome":"invalid-request","problem":{"code":"c","message":"m"}}}""";
        });

        var result = await new DiagnosticsSocketClient().CaptureAsync(server.Path, new DiagnosticCaptureRequest
        {
            Format = DiagnosticCaptureFormat.Html,
            HistoryRows = 7,
            Authorizations = [DiagnosticAuthorization.NonScreenMetadata]
        }, TestContext.Current.CancellationToken);

        StringAssert.Contains(received, "\"method\":\"capture\"");
        StringAssert.Contains(received, "\"capture\":{\"format\":\"html\",\"historyRows\":7,\"authorizations\":[\"non-screen-metadata\"]}");
        Assert.AreEqual(DiagnosticOutcome.InvalidRequest, result.Outcome, "the target's own outcome must be preserved");
        Assert.AreEqual("c", result.Problem!.Code);
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
            new DiagnosticsSocketClient(TimeSpan.FromMilliseconds(200)).SendAsync(
                server.Path, new DiagnosticsRequest { Method = "shutdown" }, TestContext.Current.CancellationToken));

        StringAssert.Contains(error.Message, "did not respond");
    }

    [TestMethod]
    public async Task Capture_CallerCancellation_Throws()
    {
        await using var server = await FakeServer.StartAsync(_ => null);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new DiagnosticsSocketClient().CaptureAsync(server.Path, new DiagnosticCaptureRequest(), cts.Token));
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
