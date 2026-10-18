using ComputeWarden.Cli;
using ComputeWarden.Client;

return await Cli.RunAsync(args, new WardenClient(connectTimeoutMs: 1500), Console.Out, Console.Error, DaemonLauncher.TryLaunch);
