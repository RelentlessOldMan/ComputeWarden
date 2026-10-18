using System.Text.Json;
using ComputeWarden.Core;
using ComputeWarden.Core.Ipc;
using ComputeWarden.Core.Model;
using ComputeWarden.Core.Providers;
using ComputeWarden.Daemon;

namespace ComputeWarden.Tests;

public class MonitorPublisherTests
{
    private sealed class FakeSink : IMonitorSink
    {
        public bool Enabled { get; init; } = true;
        public bool Started;
        public readonly List<(string Id, object? Value)> Sets = new();
        public readonly List<(string Id, string Text, string Level)> Logs = new();
        public readonly Dictionary<string, Func<JsonElement?, object?>> Provided = new();

        public void Start() => Started = true;
        public void Set(string id, object? value) => Sets.Add((id, value));
        public void Log(string id, string text, string level = "info") => Logs.Add((id, text, level));
        public void Provide(string name, Func<JsonElement?, object?> handler, string? description = null) => Provided[name] = handler;
        public void Dispose() { }

        public string? Last(string id) => Sets.LastOrDefault(s => s.Id == id).Value as string;
    }

    private static (Warden Warden, FakeSink Sink, MonitorPublisher Publisher, FakeBlockerProvider Games) NewPublisher(FakeClock clock)
    {
        var games = new FakeBlockerProvider();
        var warden = TestFactory.NewWarden(clock, null, games);
        var sink = new FakeSink();
        var publisher = new MonitorPublisher(sink, warden, new RequestDispatcher(warden), clock, TimeSpan.FromHours(1));
        return (warden, sink, publisher, games);
    }

    [Fact]
    public void Start_publishes_the_idle_machine()
    {
        var (_, sink, publisher, _) = NewPublisher(new FakeClock());

        publisher.Start();

        Assert.True(sink.Started);
        Assert.Equal("available", sink.Last("state"));
        Assert.Equal("auto", sink.Last("mode"));
        Assert.Equal("none", sink.Last("busy"));
        Assert.Equal("none", sink.Last("blockers"));
        Assert.Equal("none", sink.Last("claims"));
    }

    [Fact]
    public void Publish_shows_games_and_claims_and_sends_only_changes()
    {
        var clock = new FakeClock();
        var (warden, sink, publisher, games) = NewPublisher(clock);
        publisher.Start();
        var before = sink.Sets.Count;

        publisher.Publish();
        Assert.Equal(before, sink.Sets.Count); // nothing changed, nothing sent

        games.Result = ProviderResult.Ok(FakeBlockerProvider.Process("Game.exe", 7) with { Resources = ResourceSet.Cpu | ResourceSet.Gpu });
        warden.Acquire(new ClaimRequest("Claude Code (repo)", "dotnet test", ClaimKind.Shared, EstimateSeconds: 300));
        publisher.Publish();

        Assert.Equal("busy", sink.Last("state"));
        Assert.Equal("Game.exe is running", sink.Last("blockers"));
        Assert.Contains("cpu", sink.Last("busy"));
        var claims = sink.Last("claims")!;
        Assert.Contains("Claude Code (repo): dotnet test (shared, throttled, cpu+ram+disk", claims);
        Assert.Contains("m)", claims);
    }

    [Fact]
    public void Queued_claims_show_their_place_in_line()
    {
        var clock = new FakeClock();
        var (warden, sink, publisher, _) = NewPublisher(clock);
        warden.Acquire(new ClaimRequest("A", "first", ClaimKind.Exclusive, ResourceSet.Gpu, WhenBusy.Wait));
        warden.Acquire(new ClaimRequest("B", "second", ClaimKind.Exclusive, ResourceSet.Gpu, WhenBusy.Wait));

        publisher.Start();

        var lines = sink.Last("claims")!.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Contains("(exclusive, full, gpu", lines[0]);
        Assert.Contains("(exclusive, queued #1, gpu", lines[1]);
    }

    [Fact]
    public void Mode_text_includes_resources_and_reason()
    {
        var (warden, sink, publisher, _) = NewPublisher(new FakeClock());
        warden.SetMode(UserMode.Hold, ResourceSet.Gpu, reason: "streaming");

        publisher.Start();

        Assert.Equal("hold (gpu): streaming", sink.Last("mode"));
    }

    [Fact]
    public void Status_request_answers_like_the_pipe()
    {
        var (warden, sink, _, _) = NewPublisher(new FakeClock());
        warden.Acquire(new ClaimRequest("A", "work", ClaimKind.Shared));

        var answer = (JsonElement)sink.Provided["status"](null)!;

        Assert.Equal("AVAILABLE", answer.GetProperty("state").GetString());
        Assert.Equal(1, answer.GetProperty("claims").GetArrayLength());
    }

    [Fact]
    public void Disabled_sink_is_never_started()
    {
        var clock = new FakeClock();
        var warden = TestFactory.NewWarden(clock);
        var sink = new FakeSink { Enabled = false };
        var publisher = new MonitorPublisher(sink, warden, new RequestDispatcher(warden), clock);

        publisher.Start();

        Assert.False(sink.Started);
        Assert.Empty(sink.Sets);
    }

    [Fact]
    public void A_throwing_sink_does_not_break_publish()
    {
        var clock = new FakeClock();
        var warden = TestFactory.NewWarden(clock);
        var publisher = new MonitorPublisher(new ThrowingSink(), warden, new RequestDispatcher(warden), clock, TimeSpan.FromHours(1));

        publisher.Start();
        publisher.Publish();
        var log = new MonitorLog(new ThrowingSink());
        log.Info("claimed");
        log.Error("broke", new InvalidOperationException());
    }

    [Fact]
    public void A_failed_send_is_retried_on_the_next_publish()
    {
        var clock = new FakeClock();
        var warden = TestFactory.NewWarden(clock);
        var sink = new FlakySink { FailNext = 1 };
        var publisher = new MonitorPublisher(sink, warden, new RequestDispatcher(warden), clock, TimeSpan.FromHours(1));

        publisher.Publish(); // the first Set throws
        publisher.Publish();

        Assert.Contains(sink.Sets, s => s.Id == "state" && (string?)s.Value == "available");
    }

    private sealed class FlakySink : IMonitorSink
    {
        public int FailNext;
        public readonly List<(string Id, object? Value)> Sets = new();
        public bool Enabled => true;
        public void Start() { }
        public void Set(string id, object? value)
        {
            if (FailNext-- > 0) throw new IOException();
            Sets.Add((id, value));
        }
        public void Log(string id, string text, string level = "info") { }
        public void Provide(string name, Func<JsonElement?, object?> handler, string? description = null) { }
        public void Dispose() { }
    }

    [Fact]
    public void MonitorLog_feeds_info_and_above_with_levels()
    {
        var sink = new FakeSink();
        var log = new MonitorLog(sink);

        log.Debug("noise");
        log.Info("claimed");
        log.Warn("slow");
        log.Error("broke", new InvalidOperationException("why"));

        Assert.Equal(new[]
        {
            (MonitorPublisher.EventsFeed, "claimed", "info"),
            (MonitorPublisher.EventsFeed, "slow", "warn"),
            (MonitorPublisher.EventsFeed, "broke: why", "error"),
        }, sink.Logs);
    }

    [Fact]
    public void Bridge_without_the_client_dll_is_disabled_and_inert()
    {
        var old = Environment.GetEnvironmentVariable("RELENTLESSMONITOR_CLIENT");
        Environment.SetEnvironmentVariable("RELENTLESSMONITOR_CLIENT", Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N")));
        try
        {
            if (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RelentlessMonitor", "client", "RelentlessMonitor.Client.dll")))
                return; // installed on this machine: the bridge would rightly find it

            using var bridge = MonitorBridge.Create(MonitorPublisher.App, MonitorPublisher.Layout);

            Assert.False(bridge.Enabled);
            Assert.Contains("not found", bridge.Status);
            bridge.Start();
            bridge.Set("state", "busy");
            bridge.Log(MonitorPublisher.EventsFeed, "x");
        }
        finally
        {
            Environment.SetEnvironmentVariable("RELENTLESSMONITOR_CLIENT", old);
        }
    }

    private sealed class ThrowingSink : IMonitorSink
    {
        public bool Enabled => true;
        public void Start() => throw new InvalidOperationException();
        public void Set(string id, object? value) => throw new InvalidOperationException();
        public void Log(string id, string text, string level = "info") => throw new InvalidOperationException();
        public void Provide(string name, Func<JsonElement?, object?> handler, string? description = null) { }
        public void Dispose() { }
    }
}
