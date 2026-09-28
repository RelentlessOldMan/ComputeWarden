using System.Text.Json;
using System.Text.Json.Nodes;
using ComputeWarden.Core;
using ComputeWarden.Core.Ipc;
using ComputeWarden.Core.Providers;

namespace ComputeWarden.Tests;

public class IpcDispatcherTests
{
    private static (RequestDispatcher dispatcher, Warden warden) New(bool allowManual = true)
    {
        var warden = TestFactory.NewWarden(new FakeClock());
        return (new RequestDispatcher(warden, allowManual), warden);
    }

    private static JsonNode Handle(RequestDispatcher d, object request)
    {
        var line = JsonSerializer.Serialize(request);
        return JsonNode.Parse(d.Handle(line))!;
    }

    [Fact]
    public void Status_returns_available_when_idle()
    {
        var (d, _) = New();

        var res = Handle(d, new { v = 1, op = "STATUS" });

        Assert.True((bool)res["ok"]!);
        Assert.Equal("AVAILABLE", (string)res["state"]!);
        Assert.True((bool)res["canRunIntensive"]!);
    }

    [Fact]
    public void Acquire_then_second_acquire_is_blocked()
    {
        var (d, _) = New();

        var first = Handle(d, new { v = 1, op = "ACQUIRE", owner = "A", description = "work" });
        Assert.True((bool)first["acquired"]!);
        var id = (string)first["reservationId"]!;
        Assert.False(string.IsNullOrEmpty(id));

        var second = Handle(d, new { v = 1, op = "ACQUIRE", owner = "B", description = "work" });
        Assert.False((bool)second["acquired"]!);
        Assert.Equal("BUSY", (string)second["machineState"]!);
        Assert.Single(second["blockers"]!.AsArray());
    }

    [Fact]
    public void Full_acquire_renew_release_cycle_over_protocol()
    {
        var (d, _) = New();
        var acquire = Handle(d, new { v = 1, op = "ACQUIRE", owner = "A", description = "work", leaseSeconds = 300 });
        var id = (string)acquire["reservationId"]!;

        var renew = Handle(d, new { v = 1, op = "RENEW", reservationId = id, leaseSeconds = 120 });
        Assert.True((bool)renew["renewed"]!);

        var release = Handle(d, new { v = 1, op = "RELEASE", reservationId = id });
        Assert.True((bool)release["released"]!);

        var release2 = Handle(d, new { v = 1, op = "RELEASE", reservationId = id });
        Assert.False((bool)release2["released"]!);
    }

    [Fact]
    public void Manual_busy_set_and_clear()
    {
        var (d, _) = New();

        var set = Handle(d, new { v = 1, op = "SET_MANUAL_BUSY", reason = "disk maintenance" });
        Assert.True((bool)set["ok"]!);
        Assert.Equal("MANUAL", (string)set["blocker"]!["type"]!);

        var status = Handle(d, new { v = 1, op = "STATUS" });
        Assert.Equal("BUSY", (string)status["state"]!);

        var clear = Handle(d, new { v = 1, op = "CLEAR_MANUAL_BUSY" });
        Assert.True((bool)clear["cleared"]!);
    }

    [Fact]
    public void Manual_busy_disabled_returns_error()
    {
        var (d, _) = New(allowManual: false);

        var set = Handle(d, new { v = 1, op = "SET_MANUAL_BUSY", reason = "x" });

        Assert.False((bool)set["ok"]!);
        Assert.Contains("disabled", (string)set["error"]!);
    }

    [Fact]
    public void Oversized_manual_reason_is_a_clean_validation_error_not_internal()
    {
        var (d, _) = New();

        var res = Handle(d, new { v = 1, op = "SET_MANUAL_BUSY", reason = new string('x', 5000) });

        Assert.False((bool)res["ok"]!);
        var error = (string)res["error"]!;
        Assert.Contains("reason exceeds", error);
        Assert.DoesNotContain("Internal error", error);
    }

    [Fact]
    public void Malformed_json_returns_structured_error()
    {
        var (d, _) = New();

        var res = JsonNode.Parse(d.Handle("{ this is not json"))!;

        Assert.False((bool)res["ok"]!);
        Assert.Contains("Malformed", (string)res["error"]!);
    }

    [Fact]
    public void Unknown_op_returns_error()
    {
        var (d, _) = New();

        var res = Handle(d, new { v = 1, op = "FLY_TO_THE_MOON" });

        Assert.False((bool)res["ok"]!);
        Assert.Contains("Unknown op", (string)res["error"]!);
    }

    [Fact]
    public void Wrong_protocol_version_is_rejected()
    {
        var (d, _) = New();

        var res = Handle(d, new { v = 99, op = "STATUS" });

        Assert.False((bool)res["ok"]!);
        Assert.Contains("version", (string)res["error"]!);
    }

    [Fact]
    public void Stats_reports_counters()
    {
        var (d, _) = New();
        Handle(d, new { v = 1, op = "ACQUIRE", owner = "A", description = "work" });
        Handle(d, new { v = 1, op = "ACQUIRE", owner = "B", description = "work" }); // fails

        var stats = Handle(d, new { v = 1, op = "STATS" });

        Assert.Equal(1, (int)stats["totalAcquisitions"]!);
        Assert.Equal(1, (int)stats["failedAcquisitions"]!);
        Assert.Equal(1, (int)stats["currentReservations"]!);
    }
}
