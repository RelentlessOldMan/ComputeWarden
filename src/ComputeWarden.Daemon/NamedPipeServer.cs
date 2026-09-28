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
/// local users cannot connect (spec §26). On other platforms the default pipe is used.
/// </summary>
public sealed class NamedPipeServer : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Upper bound on a single request line; well above any legitimate request.</summary>
    private const int MaxRequestChars = 256 * 1024;

    private readonly string _pipeName;
    private readonly RequestDispatcher _dispatcher;
    private readonly ILog _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public NamedPipeServer(string pipeName, RequestDispatcher dispatcher, ILog log)
    {
        _pipeName = pipeName;
        _dispatcher = dispatcher;
        _log = log;
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

            // Handle this client concurrently; immediately loop to accept the next one.
            _ = HandleClientAsync(server);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server)
    {
        try
        {
            await using (server)
            using (var reader = new StreamReader(server, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
            using (var writer = new StreamWriter(server, Utf8NoBom) { AutoFlush = true })
            {
                string? line;
                while (!_cts.IsCancellationRequested &&
                       (line = await reader.ReadLineAsync(_cts.Token)) is not null)
                {
                    if (line.Length == 0) continue;

                    // Defense-in-depth (spec §26): a legitimate request is tiny even with maxed-out
                    // metadata. Reject absurd lines without handing them to the parser. (The pipe is
                    // also ACL-restricted to the current user, so this is a belt-and-suspenders bound.)
                    var response = line.Length > MaxRequestChars
                        ? "{\"v\":1,\"ok\":false,\"error\":\"request too large\"}"
                        : _dispatcher.Handle(line);
                    await writer.WriteLineAsync(response.AsMemory(), _cts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (IOException)
        {
            // client disconnected mid-request; expected, not an error
        }
        catch (Exception ex)
        {
            _log.Error("Client handler error", ex);
        }
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
        _cts.Dispose();
    }
}
