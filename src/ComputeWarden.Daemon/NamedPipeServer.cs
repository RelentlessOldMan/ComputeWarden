using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using ComputeWarden.Core.Diagnostics;
using ComputeWarden.Core.Ipc;

namespace ComputeWarden.Daemon;

/// <summary>
/// Accepts local named-pipe connections and serves the newline-delimited JSON protocol by
/// delegating each request line to the <see cref="RequestDispatcher"/>. Handles multiple
/// concurrent clients. A single malformed request or a client disconnect never brings the
/// server down.
///
/// On Windows the pipe DACL is restricted to the current user (plus LocalSystem) so other
/// local users cannot connect, and remote clients are denied. On other platforms the default
/// pipe is used.
/// </summary>
public sealed class NamedPipeServer : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Upper bound on a single request line; well above any legitimate request.</summary>
    private const int MaxRequestChars = 256 * 1024;

    private const string TooLargeResponse = "{\"v\":1,\"ok\":false,\"error\":\"request too large\"}";

    private readonly string _pipeName;
    private readonly RequestDispatcher _dispatcher;
    private readonly ILog _log;
    private readonly TimeSpan _idleTimeout;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<long, Task> _clients = new();
    private long _nextClientId;
    private Task? _acceptLoop;

    /// <param name="idleTimeout">
    /// How long a connected client may sit without sending a complete request before it is
    /// dropped. Clients send immediately, so this only reclaims pipe instances from stuck or
    /// misbehaving clients (each holds one of a finite number of instances).
    /// </param>
    public NamedPipeServer(string pipeName, RequestDispatcher dispatcher, ILog log, TimeSpan? idleTimeout = null)
    {
        _pipeName = pipeName;
        _dispatcher = dispatcher;
        _log = log;
        _idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(30);
    }

    public void Start()
    {
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _log.Info($"IPC listening on pipe \\\\.\\pipe\\{_pipeName}");
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = CreatePipeInstance();
            }
            catch (Exception ex)
            {
                _log.Error("Failed to create pipe instance", ex);
                try { await Task.Delay(500, _cts.Token); } catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                break;
            }
            catch (IOException ex)
            {
                // Expected transient: a client aborted before the handshake completed. The loop
                // just moves on to a fresh pipe instance — not an error worth a stack trace.
                _log.Debug($"Pipe connection aborted before handshake: {ex.Message}");
                await server.DisposeAsync();
                continue;
            }
            catch (Exception ex)
            {
                _log.Error("Pipe wait-for-connection failed", ex);
                await server.DisposeAsync();
                continue;
            }

            // Handle this client concurrently; immediately loop to accept the next one. Handlers
            // are tracked so shutdown can wait for them before disposing shared state.
            var id = Interlocked.Increment(ref _nextClientId);
            var handler = HandleClientAsync(server, id);
            _clients[id] = handler;
            // The handler removes itself when done; if it finished before we registered it,
            // its removal was a no-op, so remove it here instead.
            if (handler.IsCompleted) _clients.TryRemove(id, out _);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, long id)
    {
        // Don't run the body on the accept loop's thread.
        await Task.Yield();
        try
        {
            await using (server)
            using (var reader = new StreamReader(server, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
            using (var writer = new StreamWriter(server, Utf8NoBom, bufferSize: 1024, leaveOpen: true) { AutoFlush = true })
            {
                var lines = new BoundedLineReader(reader, MaxRequestChars);
                Task<(string? Line, bool TooLong)>? next = null;
                while (!_cts.IsCancellationRequested)
                {
                    // Defense-in-depth: a legitimate request is tiny even with maxed-out metadata,
                    // so cap line length while reading. (The pipe is also ACL-restricted to the
                    // current user; this bounds a buggy client, not just a hostile one.)
                    var read = next ?? lines.ReadLineAsync(_cts.Token);
                    next = null;
                    (string? Line, bool TooLong) got;
                    try { got = await read.WaitAsync(_idleTimeout, _cts.Token); }
                    catch (TimeoutException) { break; } // idle past the timeout; disposing the pipe ends the read

                    var (line, tooLong) = got;
                    if (tooLong)
                    {
                        _log.Warn($"Rejected pipe request exceeding {MaxRequestChars} characters");
                        await WriteAsync(writer, TooLargeResponse);
                        continue;
                    }
                    if (line is null) break;
                    if (line.Length == 0) continue;

                    // Read the next request while this one is handled: a renew can wait up to a
                    // minute for new advice, and if the client hangs up meanwhile the read ends,
                    // so the wait stops instead of renewing a lease for nobody.
                    using var gone = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    var handling = _dispatcher.HandleAsync(line, gone.Token);
                    next = lines.ReadLineAsync(_cts.Token);
                    if (await Task.WhenAny(handling, next) == next &&
                        (next.IsFaulted || next.IsCanceled || next.Result.Line is null))
                        gone.Cancel();

                    var response = await handling;
                    await WriteAsync(writer, response);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down, or the client sat idle past the timeout
        }
        catch (IOException)
        {
            // client disconnected mid-request; expected, not an error
        }
        catch (Exception ex)
        {
            _log.Error("Client handler error", ex);
        }
        finally
        {
            _clients.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Writes one response, giving up after the idle timeout: a client that stops reading would
    /// otherwise hold this handler, and one of the finite pipe instances, forever.
    /// </summary>
    private async Task WriteAsync(StreamWriter writer, string response)
    {
        using var stuck = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        stuck.CancelAfter(_idleTimeout);
        await writer.WriteLineAsync(response.AsMemory(), stuck.Token);
    }

    private NamedPipeServerStream CreatePipeInstance()
        => OperatingSystem.IsWindows()
            ? CreateSecuredPipe()
            : new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

    [SupportedOSPlatform("windows")]
    private NamedPipeServerStream CreateSecuredPipe()
    {
        var security = new PipeSecurity();

        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is { } user)
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));

        // The same user signed in on another machine would match the rule above over SMB. The
        // warden coordinates this machine only, so refuse every remote client.
        var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        security.AddAccessRule(new PipeAccessRule(network, PipeAccessRights.FullControl, AccessControlType.Deny));

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; }
            catch { /* already logged */ }
        }
        // Handlers observe _cts; let them finish before it is disposed.
        try { await Task.WhenAll(_clients.Values); }
        catch { /* each handler logs its own failures */ }
        _cts.Dispose();
    }
}
