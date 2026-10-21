using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Theoistic.PDF.Rendering.Cdp;

/// <summary>
/// A connection to a browser's DevTools Protocol endpoint.
/// <para>
/// One WebSocket carries every conversation. Commands for a particular page travel with a
/// session id (the protocol's "flat" mode), so a single connection can drive many pages
/// concurrently without a socket each.
/// </para>
/// <para>
/// Built on <see cref="ClientWebSocket"/> and <see cref="JsonNode"/> alone - the protocol is
/// small enough that a dependency would cost more than it saves.
/// </para>
/// </summary>
internal sealed class CdpConnection : IAsyncDisposable
{
    private readonly ClientWebSocket socket;
    private readonly CancellationTokenSource shutdown = new CancellationTokenSource();

    // Sends are serialised because a WebSocket permits only one send at a time, while replies
    // are matched by id so they can complete in any order.
    private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> pending = new();
    private readonly ConcurrentDictionary<long, Subscription> subscriptions = new();

    private readonly Task receiveLoop;

    private int nextCommandId;
    private long nextSubscriptionId;
    private int disposed;

    private CdpConnection(ClientWebSocket socket)
    {
        this.socket = socket;
        receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    /// <summary>Why the connection ended, if it ended on its own.</summary>
    public Exception? Fault { get; private set; }

    public bool IsOpen => socket.State == WebSocketState.Open && Fault is null;

    public static async Task<CdpConnection> ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var socket = new ClientWebSocket();

        // The protocol sends whole JSON messages and the default 4 KiB receive buffer would
        // fragment every one of them; a printToPDF reply carries the entire document base64'd.
        socket.Options.SetBuffer(receiveBufferSize: 64 * 1024, sendBufferSize: 16 * 1024);

        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new CdpConnection(socket);
    }

    /// <summary>
    /// Issues a command and waits for its reply.
    /// </summary>
    /// <param name="sessionId">
    /// The page session the command belongs to, or null to address the browser itself.
    /// </param>
    public async Task<JsonNode> SendAsync(
        string method,
        object? parameters = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        if (Fault is { } fault)
        {
            throw new PdfConversionException($"The browser connection has failed: {fault.Message}");
        }

        int id = Interlocked.Increment(ref nextCommandId);

        var message = new JsonObject
        {
            ["id"] = id,
            ["method"] = method,
        };

        if (parameters is not null)
        {
            message["params"] = JsonSerializer.SerializeToNode(parameters);
        }

        if (sessionId is not null)
        {
            message["sessionId"] = sessionId;
        }

        var completion = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;

        try
        {
            byte[] payload = Encoding.UTF8.GetBytes(message.ToJsonString());

            await sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                sendLock.Release();
            }

            using CancellationTokenRegistration registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<JsonNode>)state!).TrySetCanceled(),
                completion);

            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Registers interest in an event before the command that triggers it is sent. Waiting only
    /// after the command has gone out would lose an event that arrives in between.
    /// </summary>
    public CdpEventWaiter Expect(string method, string? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        long id = Interlocked.Increment(ref nextSubscriptionId);
        var waiter = new CdpEventWaiter(() => subscriptions.TryRemove(id, out _));

        subscriptions[id] = new Subscription(method, sessionId, waiter.Complete);

        return waiter;
    }

    /// <summary>Observes every occurrence of an event until the returned handle is disposed.</summary>
    public IDisposable Subscribe(string method, string? sessionId, Action<JsonNode> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(handler);

        long id = Interlocked.Increment(ref nextSubscriptionId);

        subscriptions[id] = new Subscription(method, sessionId, handler);

        return new Unsubscriber(() => subscriptions.TryRemove(id, out _));
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await socket.ReceiveAsync(buffer, shutdown.Token).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Finish(new PdfConversionException("The browser closed the DevTools connection."));
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                message.Position = 0;
                Dispatch(message);
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }
        catch (Exception error)
        {
            Finish(error);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Dispatch(Stream message)
    {
        JsonNode? node;

        try
        {
            node = JsonNode.Parse(message);
        }
        catch (JsonException)
        {
            // A message this client cannot parse is not worth tearing the connection down for;
            // a command waiting on a reply will time out and say so.
            return;
        }

        if (node is null)
        {
            return;
        }

        if ((int?)node["id"] is { } id)
        {
            if (pending.TryRemove(id, out TaskCompletionSource<JsonNode>? completion))
            {
                if (node["error"] is JsonNode error)
                {
                    completion.TrySetException(new PdfConversionException(
                        $"The browser rejected a DevTools command: {error["message"]?.GetValue<string>() ?? error.ToJsonString()}"));
                }
                else
                {
                    completion.TrySetResult(node["result"] ?? new JsonObject());
                }
            }

            return;
        }

        string? method = (string?)node["method"];
        string? session = (string?)node["sessionId"];

        if (method is null)
        {
            return;
        }

        foreach (Subscription subscription in subscriptions.Values)
        {
            if (subscription.Matches(method, session))
            {
                subscription.Handler(node["params"] ?? new JsonObject());
            }
        }
    }

    private void Finish(Exception error)
    {
        Fault ??= error;

        foreach (TaskCompletionSource<JsonNode> completion in pending.Values)
        {
            completion.TrySetException(error);
        }

        pending.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        shutdown.Cancel();

        try
        {
            await receiveLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The loop is being torn down; whatever it was doing no longer matters.
        }

        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));

                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closing.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // A browser that has already gone will not complete the handshake.
        }

        socket.Dispose();
        shutdown.Dispose();
        sendLock.Dispose();

        Finish(new ObjectDisposedException(nameof(CdpConnection)));
    }

    private sealed record Subscription(string Method, string? SessionId, Action<JsonNode> Handler)
    {
        public bool Matches(string method, string? sessionId) =>
            string.Equals(Method, method, StringComparison.Ordinal) &&
            (SessionId is null || string.Equals(SessionId, sessionId, StringComparison.Ordinal));
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref this.dispose, null)?.Invoke();
    }
}

/// <summary>
/// A pending wait for one protocol event, armed at construction so that nothing is missed
/// between arming and the command that causes it.
/// </summary>
internal sealed class CdpEventWaiter : IDisposable
{
    private readonly TaskCompletionSource<JsonNode> completion =
        new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);

    private Action? unsubscribe;

    internal CdpEventWaiter(Action unsubscribe) => this.unsubscribe = unsubscribe;

    internal void Complete(JsonNode parameters) => completion.TrySetResult(parameters);

    /// <summary>Waits for the event, or throws <see cref="TimeoutException"/>.</summary>
    public async Task<JsonNode> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        expiry.CancelAfter(timeout);

        using CancellationTokenRegistration registration = expiry.Token.Register(
            static state => ((TaskCompletionSource<JsonNode>)state!).TrySetCanceled(),
            completion);

        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.#}s waiting for a browser event.");
        }
    }

    public void Dispose() => Interlocked.Exchange(ref unsubscribe, null)?.Invoke();
}
