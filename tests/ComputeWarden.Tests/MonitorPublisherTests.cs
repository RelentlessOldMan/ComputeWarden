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
        public readonly Dictionary<string, Func<JsonElement?, object?>> Actions = new();
        public readonly Dictionary<string, Action<string>> Choices = new();
        public readonly Dictionary<string, Action<IReadOnlyList<string>>> Flags = new();
        public readonly Dictionary<string, Action<string>> Picks = new();
        public MonitorSender? Sender { get; set; } = new("monitor:3", "monitor", "RelentlessEye", "PC");

        public void Start() => Started = true;
        public void Set(string id, object? value) => Sets.Add((id, value));
        public void Log(string id, string text, string level = "info") => Logs.Add((id, text, level));
        public void Provide(string name, Func<JsonElement?, object?> handler, string? description = null) => Provided[name] = handler;
        public void OnAction(string widgetId, Func<JsonElement?, object?> handler) => Actions[widgetId] = handler;
        public readonly List<object> Layouts = new();
        public void SetLayout(object layout) => Layouts.Add(layout);
        public void OnChoice(string widgetId, Action<string> handler) => Choices[widgetId] = handler;
        public void OnFlags(string widgetId, Action<IReadOnlyList<string>> handler) => Flags[widgetId] = handler;
        public void OnPick(string widgetId, Action<string> handler) => Picks[widgetId] = handler;
        public void Dispose() { }

        public object? Hold(string value) => Actions[MonitorPublisher.HoldPick](JsonSerializer.SerializeToElement(value));

        public string? Last(string id) => Sets.LastOrDefault(s => s.Id == id).Value as string;
        public JsonElement Table() => JsonSerializer.SerializeToElement(Sets.Last(s => s.Id == MonitorPublisher.ClaimsTable).Value);
        public JsonElement[] Rows() => Table().GetProperty("rows").EnumerateArray().ToArray();
        public string[] Who() => Rows().Select(r => Text(r, "who")).ToArray();
        public JsonElement Row(string who) => Rows().Single(r => Text(r, "who") == who);
        public string Layout() => JsonSerializer.Serialize(Layouts.LastOrDefault() ?? MonitorPublisher.Layout); // none sent: still the start layout

        // A cell is plain text or {text, color, dot, tip}.
        public static string? Text(JsonElement row, string key)
            => !row.TryGetProperty(key, out var cell) ? null
                : cell.ValueKind == JsonValueKind.String ? cell.GetString()
                : cell.TryGetProperty("text", out var t) ? t.GetString() : null;

        public static string? Color(JsonElement row, string key)
            => row.TryGetProperty(key, out var cell) && cell.ValueKind == JsonValueKind.Object && cell.TryGetProperty("color", out var c) ? c.GetString() : null;
    }

    private static (Warden Warden, FakeSink Sink, MonitorPublisher Publisher, FakeBlockerProvider Games) NewPublisher(
        FakeClock clock, CapturingLog? log = null)
    {
        var games = new FakeBlockerProvider();
        var warden = TestFactory.NewWarden(clock, null, games);
        var sink = new FakeSink();
        var publisher = new MonitorPublisher(sink, warden, new RequestDispatcher(warden), clock, TimeSpan.FromHours(1), log);
        return (warden, sink, publisher, games);
    }

    [Fact]
    public void Start_publishes_the_idle_machine()
    {
        var (_, sink, publisher, _) = NewPublisher(new FakeClock());

        publisher.Start();

        Assert.True(sink.Started);
        Assert.Equal("available", sink.Last("state"));
        Assert.Equal("off", sink.Last(MonitorPublisher.HoldPick));
        Assert.Equal("none", sink.Last("blockers"));
        Assert.Empty(sink.Rows());
        Assert.DoesNotContain("\"mode\"", sink.Layout());
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

        games.Result = ProviderResult.Ok(FakeBlockerProvider.Process("Game.exe", 7));
        warden.Acquire(new ClaimRequest("Claude Code (repo)", "dotnet test", ClaimKind.Shared, EstimateSeconds: 300));
        publisher.Publish();

        Assert.Equal("busy", sink.Last("state"));
        Assert.Equal("Game.exe is running", sink.Last("blockers"));
        var row = sink.Row("repo");
        Assert.Equal("running throttled", FakeSink.Text(row, "state"));
        Assert.Equal("orange", FakeSink.Color(row, "state"));
        Assert.Equal("dotnet test", FakeSink.Text(row, "what"));
        Assert.Matches("^[0-9]+m$", FakeSink.Text(row, "lease"));
    }

    [Fact]
    public void Queued_claims_show_their_place_in_line_and_kind()
    {
        var clock = new FakeClock();
        var (warden, sink, publisher, _) = NewPublisher(clock);
        warden.Acquire(new ClaimRequest("A", "first", BigMemory: true));
        warden.Acquire(new ClaimRequest("T", "bench", ClaimKind.Solo, EstimateSeconds: 60));
        warden.Acquire(new ClaimRequest("B", "second", BigMemory: true));

        publisher.Start();

        Assert.Equal(new[] { "A", "T", "B" }, sink.Who());
        Assert.Equal("running full, big memory", FakeSink.Text(sink.Row("A"), "state"));
        Assert.Equal("green", FakeSink.Color(sink.Row("A"), "state"));
        Assert.Equal("queued #1, solo", FakeSink.Text(sink.Row("T"), "state"));
        Assert.Equal("queued #2, big memory", FakeSink.Text(sink.Row("B"), "state"));
        Assert.Equal("gray", FakeSink.Color(sink.Row("B"), "state"));
    }

    [Fact]
    public void Claim_rows_say_whose_turn_it_is_and_which_havent_checked_in()
    {
        var clock = new FakeClock();
        var (warden, sink, publisher, _) = NewPublisher(clock);
        var a = warden.Acquire(new ClaimRequest("A", "a", BigMemory: true)).ReservationId!;
        warden.Acquire(new ClaimRequest("B", "b", BigMemory: true));
        warden.Release(a);
        warden.Acquire(new ClaimRequest("C", "c", ClaimKind.Shared, EstimateSeconds: 3600));

        publisher.Start();

        Assert.Equal(new[] { "C", "B" }, sink.Who()); // running first, then its-turn
        Assert.Equal("its turn, not started, big memory", FakeSink.Text(sink.Row("B"), "state"));
        Assert.Equal("blue", FakeSink.Color(sink.Row("B"), "state"));
        Assert.StartsWith("running ", FakeSink.Text(sink.Row("C"), "state"));
        Assert.Equal("just now", FakeSink.Text(sink.Row("C"), "checked"));

        clock.Advance(MonitorPublisher.NoCheckInAfter + TimeSpan.FromMinutes(1));
        publisher.Publish();
        Assert.Equal("16m ago", FakeSink.Text(sink.Row("C"), "checked"));
        Assert.Matches("^[0-9]+m$", FakeSink.Text(sink.Row("C"), "lease"));
    }

    [Fact]
    public void Claim_rows_shorten_owners_and_keep_the_whole_description()
    {
        var clock = new FakeClock();
        var (warden, sink, publisher, _) = NewPublisher(clock);
        var longText = new string('x', 100);
        warden.Acquire(new ClaimRequest("Claude Code (Repo)", longText, ClaimKind.Shared, EstimateSeconds: 300));
        warden.Acquire(new ClaimRequest("codex", "lint", ClaimKind.Shared, EstimateSeconds: 300));

        publisher.Start();

        Assert.Equal(new[] { "Repo", "codex" }, sink.Who());
        var row = sink.Row("Repo");
        Assert.Equal("Claude Code (Repo)", row.GetProperty("who").GetProperty("tip").GetString());
        Assert.Equal(longText, FakeSink.Text(row, "what")); // the panel cuts it to fit, with a tooltip
        Assert.Equal(32, row.GetProperty("id").GetString()!.Length);
    }

    [Fact]
    public void Panel_turns_the_hold_on_and_off()
    {
        var log = new CapturingLog();
        var (warden, sink, publisher, _) = NewPublisher(new FakeClock(), log);
        publisher.Start();

        sink.Hold("on");

        Assert.Contains(warden.GetStatus().Blockers, b => b.Type == BlockerType.Manual && b.Description == "held from the panel");
        Assert.Equal("on", sink.Last(MonitorPublisher.HoldPick)); // the panel shows the daemon's real hold at once
        Assert.Equal("busy", sink.Last("state"));
        Assert.True(log.Contains("Hold from RelentlessEye (monitor:3) on the monitor panel"));

        sink.Hold("off");
        Assert.DoesNotContain(warden.GetStatus().Blockers, b => b.Type == BlockerType.Manual);
        Assert.Equal("off", sink.Last(MonitorPublisher.HoldPick));
        Assert.Throws<ArgumentException>(() => sink.Hold("maybe"));
        Assert.Throws<ArgumentException>(() => sink.Actions[MonitorPublisher.HoldPick](null));
    }

    [Fact]
    public void Panel_sets_the_jobs_while_gaming()
    {
        var log = new CapturingLog();
        var (warden, sink, publisher, _) = NewPublisher(new FakeClock(), log);
        publisher.Start();
        Assert.Equal("3", sink.Last(MonitorPublisher.GameJobsPick)); // the config's slots to start with

        sink.Actions[MonitorPublisher.GameJobsPick](JsonSerializer.SerializeToElement("1"));

        Assert.Equal(1, warden.GameJobs);
        Assert.Equal("1", sink.Last(MonitorPublisher.GameJobsPick));
        Assert.True(log.Contains("Jobs while gaming change from RelentlessEye (monitor:3) on the monitor panel"));

        Assert.Throws<ArgumentException>(() => sink.Actions[MonitorPublisher.GameJobsPick](JsonSerializer.SerializeToElement("9")));
        sink.Sender = new MonitorSender("PC/SomeAgent", "app", "SomeAgent", "PC");
        Assert.Throws<InvalidOperationException>(() => sink.Actions[MonitorPublisher.GameJobsPick](JsonSerializer.SerializeToElement("4")));
        Assert.Equal(1, warden.GameJobs);
    }

    [Fact]
    public void Only_a_monitor_can_set_the_hold_never_another_app()
    {
        var log = new CapturingLog();
        var (warden, sink, publisher, _) = NewPublisher(new FakeClock(), log);
        publisher.Start();

        sink.Sender = new MonitorSender("PC/SomeAgent", "app", "SomeAgent", "PC");
        Assert.Throws<InvalidOperationException>(() => sink.Hold("on"));
        sink.Sender = null; // an old client or hub can't say who sent it
        Assert.Throws<InvalidOperationException>(() => sink.Hold("on"));

        Assert.Empty(warden.GetStatus().Blockers);
        Assert.True(log.Contains("Refused a request from SomeAgent (PC/SomeAgent) to set the hold"));
        Assert.True(log.Contains("Refused a request from an unknown sender to set the hold"));
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
        public void OnAction(string widgetId, Func<JsonElement?, object?> handler) { }
        public void SetLayout(object layout) { }
        public void OnChoice(string widgetId, Action<string> handler) { }
        public void OnFlags(string widgetId, Action<IReadOnlyList<string>> handler) { }
        public void OnPick(string widgetId, Action<string> handler) { }
        public MonitorSender? Sender => null;
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
        var old = Environment.GetEnvironmentVariable("RELENTLESSEYE_CLIENT");
        Environment.SetEnvironmentVariable("RELENTLESSEYE_CLIENT", Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N")));
        try
        {
            if (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RelentlessEye", "client", "RelentlessEye.Client.dll")))
                return; // installed on this machine: the bridge would rightly find it

            using var bridge = MonitorBridge.Create(MonitorPublisher.App, MonitorPublisher.Layout);

            Assert.False(bridge.Enabled);
            Assert.Contains("not found", bridge.Status);
            bridge.Start();
            bridge.Set("state", "busy");
            bridge.Log(MonitorPublisher.EventsFeed, "x");
            bridge.OnChoice("x", _ => { });
            bridge.OnPick(MonitorPublisher.ClaimsTable, _ => { });
            Assert.Null(bridge.Sender);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RELENTLESSEYE_CLIENT", old);
        }
    }

    private sealed class ThrowingSink : IMonitorSink
    {
        public bool Enabled => true;
        public void Start() => throw new InvalidOperationException();
        public void Set(string id, object? value) => throw new InvalidOperationException();
        public void Log(string id, string text, string level = "info") => throw new InvalidOperationException();
        public void Provide(string name, Func<JsonElement?, object?> handler, string? description = null) { }
        public void OnAction(string widgetId, Func<JsonElement?, object?> handler) { }
        public void SetLayout(object layout) { }
        public void OnChoice(string widgetId, Action<string> handler) { }
        public void OnFlags(string widgetId, Action<IReadOnlyList<string>> handler) { }
        public void OnPick(string widgetId, Action<string> handler) { }
        public MonitorSender? Sender => null;
        public void Dispose() { }
    }
}
