using ComputeWarden.Daemon;

namespace ComputeWarden.Tests;

public class FileLogTests
{
    [Fact]
    public void Writes_lines_and_respects_level()
    {
        var path = Path.Combine(Path.GetTempPath(), "cw-log-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using (var log = new FileLog(path, level: "info"))
            {
                log.Debug("debug-should-be-filtered");
                log.Info("hello-info");
                log.Error("boom");
            }

            var text = File.ReadAllText(path);
            Assert.Contains("hello-info", text);
            Assert.Contains("boom", text);
            Assert.DoesNotContain("debug-should-be-filtered", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Rolls_over_when_size_cap_exceeded()
    {
        var path = Path.Combine(Path.GetTempPath(), "cw-log-" + Guid.NewGuid().ToString("N") + ".log");
        var backup = path + ".1";
        try
        {
            using (var log = new FileLog(path, level: "info", maxBytes: 200))
            {
                for (var i = 0; i < 50; i++) log.Info($"line {i} padding padding padding");
            }

            Assert.True(File.Exists(backup), "expected a rolled-over backup file");
            Assert.True(new FileInfo(path).Length <= new FileInfo(backup).Length + 500);
        }
        finally
        {
            File.Delete(path);
            File.Delete(backup);
        }
    }
}
