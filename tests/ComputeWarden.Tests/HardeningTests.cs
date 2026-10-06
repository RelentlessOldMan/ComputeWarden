using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ComputeWarden.Client;
using ComputeWarden.Core;
using ComputeWarden.Core.Config;
using ComputeWarden.Core.Diagnostics;
using ComputeWarden.Core.Ipc;
using ComputeWarden.Core.Model;
using ComputeWarden.Core.Providers;
using ComputeWarden.Daemon;
using ComputeWarden.Mcp;

namespace ComputeWarden.Tests;

/// <summary>Regression tests for defects found in the 2026-10-06 hardening pass.</summary>
public class BoundedLineReaderTests
{
    private static BoundedLineReader Reader(string text, int max = 16)
        => new(new StringReader(text), max);

    [Fact]
    public async Task Reads_lines_stripping_LF_and_CRLF()
    {
        var r = Reader("one\r\ntwo\nthree");

        Assert.Equal(("one", false), await r.ReadLineAsync(default));
        Assert.Equal(("two", false), await r.ReadLineAsync(default));
        Assert.Equal(("three", false), await r.ReadLineAsync(default)); // final line without newline
        Assert.Equal(((string?)null, false), await r.ReadLineAsync(default));
    }

    [Fact]
    public async Task Oversized_line_is_discarded_and_the_stream_resynchronizes()
    {
        var r = Reader(new string('x', 100) + "\nok\n");

        Assert.Equal(((string?)null, true), await r.ReadLineAsync(default));
        Assert.Equal(("ok", false), await r.ReadLineAsync(default));
    }

    [Fact]
    public async Task Oversized_line_at_end_of_stream_reports_too_long()
        => Assert.Equal(((string?)null, true), await Reader(new string('x', 100)).ReadLineAsync(default));

    [Fact]
    public async Task Line_spanning_internal_buffer_boundaries_is_reassembled()
    {
        var longLine = new string('a', 3000) + "b";
        var r = new BoundedLineReader(new StringReader(longLine + "\nnext\n"), 10_000);

        Assert.Equal((longLine, false), await r.ReadLineAsync(default));
        Assert.Equal(("next", false), await r.ReadLineAsync(default));
    }

    [Fact]
    public async Task Line_exactly_at_the_limit_is_accepted()
        => Assert.Equal((new string('x', 16), false), await Reader(new string('x', 16) + "\n").ReadLineAsync(default));
}

public class PipeServerHardeningTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static NamedPipeServer StartServer(string pipeName, TimeSpan idleTimeout)
    {
        var server = new NamedPipeServer(
            pipeName, new RequestDispatcher(TestFactory.NewWarden(new FakeClock())), NullLog.Instance, idleTimeout);
        server.Start();
        return server;
    }

    [Fact]
    public async Task Idle_client_is_disconnected_after_the_idle_timeout()
    {
        var pipeName = "ComputeWarden-test-" + Guid.NewGuid().ToString("N");
        await using var server = StartServer(pipeName, TimeSpan.FromMilliseconds(200));

        using var idle = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await idle.ConnectAsync(3000);

        // The server closes its end; our read then completes with end-of-stream.
        var read = idle.ReadAsync(new byte[1]).AsTask();
        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(read, finished);
        Assert.Equal(0, await read);
    }

    [Fact]
    public async Task Dispose_completes_promptly_with_a_connected_idle_client()
    {
        var pipeName = "ComputeWarden-test-" + Guid.NewGuid().ToString("N");
        var server = StartServer(pipeName, TimeSpan.FromMinutes(5));

        using var idle = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await idle.ConnectAsync(3000);
        await Task.Delay(100); // let the server pick up the connection

        var dispose = server.DisposeAsync().AsTask();
        Assert.Same(dispose, await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(5))));
        await dispose; // and without throwing (e.g. ObjectDisposedException from a live handler)
    }

    [Fact]
    public async Task Requests_on_one_connection_are_served_in_order_after_a_rejected_oversized_one()
    {
        var pipeName = "ComputeWarden-test-" + Guid.NewGuid().ToString("N");
        await using var server = StartServer(pipeName, TimeSpan.FromSeconds(30));

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(3000);
        using var reader = new StreamReader(client, Utf8NoBom, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(client, Utf8NoBom, 1024, leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync(new string('x', 300_000));
        var rejected = JsonNode.Parse((await reader.ReadLineAsync())!)!;
        await writer.WriteLineAsync("{\"v\":1,\"op\":\"STATUS\"}");
        var status = JsonNode.Parse((await reader.ReadLineAsync())!)!;

        Assert.Contains("too large", (string)rejected["error"]!);
        Assert.True((bool)status["ok"]!);
    }
}

public class ClientHardeningTests
{
    [Fact]
    public async Task Unresponsive_daemon_times_out_instead_of_hanging()
    {
        var pipeName = "ComputeWarden-test-" + Guid.NewGuid().ToString("N");
        // A "daemon" that accepts the connection but never answers.
        await using var mute = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accept = mute.WaitForConnectionAsync();

        var client = new WardenClient(pipeName, connectTimeoutMs: 3000, responseTimeout: TimeSpan.FromMilliseconds(300));
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => client.StatusAsync());

        Assert.Contains("did not respond", ex.Message);
        await accept;
    }

    [Fact]
    public async Task Caller_cancellation_is_reported_as_cancellation_not_timeout()
    {
        var pipeName = "ComputeWarden-test-" + Guid.NewGuid().ToString("N");
        await using var mute = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accept = mute.WaitForConnectionAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var client = new WardenClient(pipeName, connectTimeoutMs: 3000, responseTimeout: TimeSpan.FromSeconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.StatusAsync(cts.Token));
        await accept;
    }

    [Fact]
    public async Task Probe_reports_not_running_when_nothing_listens()
    {
        var client = new WardenClient("ComputeWarden-test-" + Guid.NewGuid().ToString("N"), connectTimeoutMs: 100);

        Assert.Equal(DaemonProbe.NotRunning, await client.ProbeAsync());
        Assert.False(await client.IsDaemonRunningAsync());
    }
}

[Collection("mcp-gateway")]
public class GatewayHardeningTests
{
    [Fact]
    public async Task Tool_reports_actionable_error_quickly_when_daemon_cannot_be_launched()
    {
        var unused = new WardenClient("ComputeWarden-test-" + Guid.NewGuid().ToString("N"), connectTimeoutMs: 100);
        WardenGateway.Configure(unused, autoSpawn: true, launch: () => false);
        try
        {
            var started = DateTime.UtcNow;
            var res = JsonNode.Parse(await ComputeWardenTools.Status())!;

            Assert.Contains("could not be launched", (string)res["error"]!);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "should fail fast, not poll");
        }
        finally
        {
            WardenGateway.Configure(new WardenClient(connectTimeoutMs: 2000), autoSpawn: true);
        }
    }
}

public class FileLogHardeningTests
{
    [Fact]
    public void Failed_rollover_keeps_logging_and_rolls_once_the_file_is_free()
    {
        var path = Path.Combine(Path.GetTempPath(), "cw-log-" + Guid.NewGuid().ToString("N") + ".log");
        var backup = path + ".1";
        try
        {
            using var log = new FileLog(path, level: "info", maxBytes: 200);

            // A reader without delete sharing (like some tail tools) blocks the rename.
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                for (var i = 0; i < 20; i++) log.Info($"while-locked {i} padding padding padding");
            }
            Assert.False(File.Exists(backup));

            log.Info("after-unlock padding padding padding");
            Assert.True(File.Exists(backup), "rollover should succeed once the file is free");

            log.Info("still-logging");
            log.Dispose();
            Assert.Contains("still-logging", File.ReadAllText(path));
            Assert.Contains("while-locked 19", File.ReadAllText(backup));
        }
        finally
        {
            File.Delete(path);
            File.Delete(backup);
        }
    }

    [Fact]
    public void Writes_after_dispose_are_ignored()
    {
        var path = Path.Combine(Path.GetTempPath(), "cw-log-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            var log = new FileLog(path);
            log.Dispose();
            log.Info("after-dispose");

            Assert.DoesNotContain("after-dispose", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class ValidationHardeningTests
{
    [Fact]
    public void Null_metadata_value_is_a_validation_error_not_an_internal_error()
    {
        var d = new RequestDispatcher(TestFactory.NewWarden(new FakeClock()));

        var res = JsonNode.Parse(d.Handle("""{"v":1,"op":"ACQUIRE","owner":"A","description":"w","metadata":{"k":null}}"""))!;

        Assert.True((bool)res["ok"]!);
        Assert.False((bool)res["acquired"]!);
        Assert.Contains("metadata value for 'k'", (string)res["reason"]!);
    }

    [Theory]
    [InlineData(0, 0, 1)]     // zero minimum: still at least 1s
    [InlineData(-5, -10, 1)]  // negative everything
    [InlineData(0, 45, 45)]
    public void Lease_is_never_zero_or_negative(int minimum, int requested, int expected)
        => Assert.Equal(expected, new LeaseOptions { MinimumSeconds = minimum, MaximumSeconds = 100 }.Resolve(requested));

    [Theory]
    [InlineData(@"C:\Program Files\Game\Game.exe")]
    [InlineData("/usr/bin/Game")]
    [InlineData("  game.EXE ")]
    public void Process_rule_matches_when_written_as_a_path_or_with_odd_casing(string executable)
    {
        var lister = new FixedLister(new RunningProcess(7, "Game"));
        var provider = new ProcessBlockerProvider(
            lister, new[] { new ProcessRule { Name = "Game", Executable = executable } },
            new ProcessDetectionOptions(), new FakeClock());

        provider.Refresh();

        Assert.Single(provider.GetBlockers().Blockers);
    }

    private sealed class FixedLister(params RunningProcess[] processes) : IProcessLister
    {
        public IReadOnlyList<RunningProcess> List() => processes;
    }
}

public class ConsoleLogHardeningTests
{
    private sealed class ThrowingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => throw new IOException("pipe is broken");
        public override void WriteLine(string? value) => throw new IOException("pipe is broken");
    }

    [Fact]
    public void Console_write_failure_never_reaches_the_caller()
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        try
        {
            Console.SetOut(new ThrowingWriter());
            Console.SetError(new ThrowingWriter());
            var log = new ConsoleLog("debug");

            log.Info("x");
            log.Error("y", new InvalidOperationException());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }
}
