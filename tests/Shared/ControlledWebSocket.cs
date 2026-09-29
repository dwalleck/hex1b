// Copyright (c) Hex1b contributors. Licensed under the MIT license.
//
// A WebSocket whose state and send failures a test controls, so a WebSocket presentation can be
// driven without a network. Linked into every *.Tests project via Directory.Build.props.

using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Hex1b.Testing;

internal sealed class ControlledWebSocket : WebSocket
{
    public WebSocketState CurrentState { get; set; } = WebSocketState.Open;

    public bool FailSends { get; set; }

    /// <summary>Completes each send asynchronously, as a real socket under backpressure does, and records nothing.</summary>
    public bool YieldSends { get; set; }

    public List<byte[]> Sent { get; } = [];

    public override WebSocketCloseStatus? CloseStatus => null;

    public override string? CloseStatusDescription => null;

    public override WebSocketState State => CurrentState;

    public override string? SubProtocol => null;

    public override void Abort() { }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Dispose() { }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(System.ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new System.OperationCanceledException();
    }

    public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(System.Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new System.OperationCanceledException();
    }

    public override Task SendAsync(System.ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
        SendAsync(buffer.AsMemory(), messageType, endOfMessage, cancellationToken).AsTask();

    public override ValueTask SendAsync(System.ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken = default)
    {
        if (YieldSends)
            return YieldThenSendAsync(buffer);
        lock (Sent)
            Sent.Add(buffer.ToArray());
        if (FailSends)
            throw new WebSocketException("The remote party closed the connection.");
        return ValueTask.CompletedTask;
    }

    private static async ValueTask YieldThenSendAsync(System.ReadOnlyMemory<byte> buffer)
    {
        await Task.Yield();
    }
}
