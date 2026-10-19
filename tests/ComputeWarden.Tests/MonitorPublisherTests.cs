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

        public object? Pick(string mode) => Actions[MonitorPublisher.ModePick](JsonSerializer.SerializeToElement(mode));

        public string? Last(string id) => Sets.LastOrDefault(s => s.Id == id).Value as string;
        public JsonElement Table() => JsonSerializer.SerializeToElement(Sets.Last(s => s.Id == MonitorPublisher.ClaimsTable).Value);
        public JsonElement[] Rows() => Table().GetProperty("rows").EnumerateArray().ToArray();
        public string[] Who() => Rows().Select(r => Text(r, "who")).ToArray();
        public JsonElement Row(string who) => Rows().Single(r => Text(r, "who") == who);
        public string? Picked() => Table().TryGetProperty("picked", out var p) ? p.GetString() : null;
        public string Layout() => JsonSerializer.Serialize(Layouts.LastOrDefault() ?? MonitorPublisher.Layout); // none sent: still the start layout

        // A cell is plain text or {text, color, dot, tip}.
        public static string? Text(JsonElement row, string key)
            => !row.TryGetProperty(key, out var cell) ? null
                : cell.ValueKind == JsonValueKind.String ? cell.GetString()
                : cell.TryGetProperty("text", out var t) ? t.GetString() : null;

        public static string? Dot(JsonElement row, string key)
            => row.TryGetProperty(key, out var cell) && cell.TryGetProperty("dot", out _) ? cell.GetProperty("color").GetString() : null;
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
        Assert.Equal("auto", sink.Last("mode"));
        Assert.Equal("auto", sink.Last("mode_detail"));
        Assert.Equal("none", sink.Last("busy"));
        Assert.Equal("none", sink.Last("blockers"));
        Assert.Empty(sink.Rows());
        Assert.Null(sink.Picked());
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
        var row = sink.Row("repo");
        Assert.Equal("running throttled", FakeSink.Text(row, "state"));
        Assert.Equal("dotnet test", FakeSink.Text(row, "what"));
        Assert.Matches("^[0-9]+m$", FakeSink.Text(row, "lease"));
        // A dot under each resource it needs, orange while throttled.
        Assert.Equal(new[] { "orange", null, "orange", null, "orange" },
            new[] { "cpu", "gpu", "ram", "net", "disk" }.Select(k => FakeSink.Dot(row, k)));
    }

    [Fact]
    public void Queued_claims_show_their_place_in_line()
    {
        var clock = new FakeClock();
        var (warden, sink, publisher, _) = NewPublisher(clock);
        warden.Acquire(new ClaimRequest("A", "first", ClaimKind.Exclusive, ResourceSet.Gpu, WhenBusy.Wait));
        warden.Acquire(new ClaimRequest("B", "second", ClaimKind.Exclusive, ResourceSet.Gpu, WhenBusy.Wait));

        publisher.Start();

        Assert.Equal(new[] { "A", "B" }, sink.Who());
        Assert.Equal("running full, exclusive", FakeSink.Text(sink.Row("A"), "state"));
        Assert.Equal("green", FakeSink.Dot(sink.Row("A"), "gpu"));
        Assert.Null(FakeSink.Dot(sink.Row("A"), "cpu"));
        Assert.Equal("queued #1, exclusive", FakeSink.Text(sink.Row("B"), "state"));
        Assert.Equal("gray", FakeSink.Dot(sink.Row("B"), "gpu"));
    }

    [Fact]
    public void Panel_shows_move_buttons_only_while_jobs_wait_and_moves_the_picked_row()
    {
        var clock = new FakeClock();
        var log = new CapturingLog();
        var (warden, sink, publisher, _) = NewPublisher(clock, log);
        var a = warden.Acquire(new ClaimRequest("Claude Code (A)", "a", ClaimKind.Exclusive, ResourceSet.Gpu, WhenBusy.Wait)).ReservationId!;
        publisher.Start();
        Assert.DoesNotContain("line_front", sink.Layout());

        var b = warden.Acquire(new ClaimRequest("Claude Code (B)", "b", ClaimKind.Exclusive, ResourceSet.Gpu, WhenBusy.Wait)).ReservationId!;
        var c = warden.Acquire(new ClaimRequest("Claude Code (C)", "c", ClaimKind.Exclusive, ResourceSet.Gpu, WhenBusy.Wait)).ReservationId!;
        publisher.Publish();
        Assert.Contains("line_front", sink.Layout());
        Assert.Equal(new[] { "A", "B", "C" }, sink.Who());
        Assert.Equal("queued #2, exclusive", FakeSink.Text(sink.Row("C"), "state"));

        Assert.Throws<InvalidOperationException>(() => publisher.MoveJob(LineMove.Up)); // nothing picked yet
        Assert.Throws<InvalidOperationException>(() => sink.Picks[MonitorPublisher.ClaimsTable](a)); // running: can't move
        sink.Picks[MonitorPublisher.ClaimsTable](c);
        sink.Actions["line_front"](null);

        Assert.Equal(new[] { c, b }, warden.GetWaiting().Select(w => w.Id));
        Assert.Equal(new[] { "A", "C", "B" }, sink.Who()); // the table follows the line
        Assert.Equal(c, sink.Picked()); // and the pick follows the job
        Assert.True(log.Contains("Line change from RelentlessEye (monitor:3) on the monitor panel"));

        // When the line empties, the buttons go and the pick is dropped.
        warden.Release(b);
        warden.Release(c);
        publisher.Publish();
        Assert.DoesNotContain("line_front", sink.Layout());
        Assert.Null(sink.Picked());
    }

    [Fact]
    public void Only_a_monitor_can_reorder_the_line()
    {
        var (warden, sink, publisher, _) = NewPublisher(new FakeClock());
        warden.Acquire(new ClaimRequest("A", "a", ClaimKind.Exclusive, ResourceSet.Gpu, WhenBusy.Wait));
        var b = warden.Acquire(new ClaimRequest("B", "b", ClaimKind.Exclusive, ResourceSet.Gpu, WhenBusy.Wait)).ReservationId!;
        warden.Acquire(new ClaimRequest("C", "c", ClaimKind.Exclusive, ResourceSet.Gpu, WhenBusy.Wait));
        publisher.Start();
        sink.Picks[MonitorPublisher.ClaimsTable](b);

        sink.Sender = new MonitorSender("PC/SomeAgent", "app", "SomeAgent", "PC");
        Assert.Throws<InvalidOperationException>(() => sink.Actions["line_down"](null));
        Assert.Throws<InvalidOperationException>(() => sink.Picks[MonitorPublisher.ClaimsTable](b));
        Assert.Equal("B", warden.GetWaiting()[0].Owner);
    }

    [Fact]
    public void Claim_rows_say_whose_turn_it_is_and_which_have_gone_quiet()
    {
        var clock = new FakeClock();
        var (warden, sink, publisher, _) = NewPublisher(clock);
        var a = warden.Acquire(new ClaimRequest("A", "a", ClaimKind.Exclusive, ResourceSet.Gpu, WhenBusy.Wait)).ReservationId!;
        warden.Acquire(new ClaimRequest("B", "b", ClaimKind.Exclusive, ResourceSet.Gpu, WhenBusy.Wait));
        warden.Release(a);
        warden.Acquire(new ClaimRequest("C", "c", ClaimKind.Shared, EstimateSeconds: 3600));

        publisher.Start();

        Assert.Equal(new[] { "C", "B" }, sink.Who()); // running first, then its-turn
        Assert.Equal("its turn, not started, exclusive", FakeSink.Text(sink.Row("B"), "state"));
        Assert.Equal("blue", FakeSink.Dot(sink.Row("B"), "gpu"));
        Assert.StartsWith("running ", FakeSink.Text(sink.Row("C"), "state"));
        Assert.DoesNotContain("quiet", FakeSink.Text(sink.Row("C"), "lease"));

        clock.Advance(MonitorPublisher.QuietAfter + TimeSpan.FromMinutes(1));
        publisher.Publish();
        Assert.EndsWith(", quiet 16m", FakeSink.Text(sink.Row("C"), "lease"));
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
    public void Mode_text_includes_resources_and_reason()
    {
        var (warden, sink, publisher, _) = NewPublisher(new FakeClock());
        warden.SetMode(UserMode.Hold, ResourceSet.Gpu, reason: "streaming");

        publisher.Start();

        Assert.Equal("hold", sink.Last("mode"));
        Assert.Equal("hold (gpu): streaming", sink.Last("mode_detail"));
    }

    [Fact]
    public void Panel_sets_the_mode_for_the_whole_machine_until_changed()
    {
        var clock = new FakeClock();
        var log = new CapturingLog();
        var (warden, sink, publisher, _) = NewPublisher(clock, log);
        publisher.Start();

        sink.Pick("steady");

        var mode = warden.GetMode();
        Assert.Equal(UserMode.Steady, mode.Mode);
        Assert.Equal(ResourceSet.All, mode.Resources);
        Assert.Null(mode.ExpiresAt);
        Assert.Equal("steady", sink.Last("mode")); // the panel shows the daemon's real mode at once
        Assert.True(log.Contains("Mode change from RelentlessEye (monitor:3) on the monitor panel"));

        sink.Pick("auto");
        Assert.Equal(UserMode.Auto, warden.GetMode().Mode);
        Assert.Equal("auto", sink.Last("mode"));
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
    public void Only_a_monitor_can_set_the_mode_never_another_app()
    {
        var log = new CapturingLog();
        var (warden, sink, publisher, _) = NewPublisher(new FakeClock(), log);
        publisher.Start();

        sink.Sender = new MonitorSender("PC/SomeAgent", "app", "SomeAgent", "PC");
        Assert.Throws<InvalidOperationException>(() => sink.Pick("hold"));
        sink.Sender = null; // an old client or hub can't say who sent it
        Assert.Throws<InvalidOperationException>(() => sink.Pick("hold"));

        Assert.Equal(UserMode.Auto, warden.GetMode().Mode);
        Assert.True(log.Contains("Refused a request from SomeAgent (PC/SomeAgent) to set the mode"));
        Assert.True(log.Contains("Refused a request from an unknown sender to set the mode"));
    }

    [Fact]
    public void Panel_refuses_an_unknown_mode()
    {
        var (warden, sink, _, _) = NewPublisher(new FakeClock());

        Assert.Throws<ArgumentException>(() => sink.Pick("turbo"));
        Assert.Throws<ArgumentException>(() => sink.Actions["mode"](null));
        Assert.Equal(UserMode.Auto, warden.GetMode().Mode);
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
