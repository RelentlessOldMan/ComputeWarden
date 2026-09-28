# Publishes the ComputeWarden daemon and MCP adapter as self-contained single-file
# executables into ./publish. Both go in the same folder so the adapter can find and
# auto-spawn the daemon (ComputeWarden.Daemon.exe alongside ComputeWarden.Mcp.exe).
param(
    [string]$Runtime = "win-x64",
    [string]$Config  = "Release",
    # Also copy the published exes to the stable per-user install dir the user-scope MCP
    # server points at (%LOCALAPPDATA%\ComputeWarden\bin), so updating is a one-liner.
    [switch]$Install
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out  = Join-Path $root "publish"

$common = @(
    "-c", $Config,
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-o", $out
)

Write-Host "Publishing daemon -> $out"
dotnet publish (Join-Path $root "src/ComputeWarden.Daemon/ComputeWarden.Daemon.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "daemon publish failed" }

Write-Host "Publishing MCP adapter -> $out"
dotnet publish (Join-Path $root "src/ComputeWarden.Mcp/ComputeWarden.Mcp.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "mcp publish failed" }

if ($Install) {
    $bin = Join-Path $env:LOCALAPPDATA "ComputeWarden\bin"
    Write-Host "Installing to $bin"
    New-Item -ItemType Directory -Force $bin | Out-Null
    # A running daemon locks its exe; stop it so the copy can replace it.
    Get-CimInstance Win32_Process -Filter "Name='ComputeWarden.Daemon.exe'" |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

    # The adapter exe stays locked while any Claude/Codex session is connected (user-scope MCP
    # spawns it per session). Copy file-by-file, skipping anything locked with a warning, so an
    # update still lands the daemon + unlocked files instead of failing outright.
    $locked = @()
    Get-ChildItem $out -File | ForEach-Object {
        $name = $_.Name
        try { Copy-Item $_.FullName (Join-Path $bin $name) -Force -ErrorAction Stop }
        catch { $locked += $name }
    }
    Write-Host ""
    if ($locked.Count -gt 0) {
        Write-Warning "Could not replace (in use by an open session): $($locked -join ', ')"
        Write-Warning "Close Claude/Codex sessions and re-run -Install to update those."
    }
    Write-Host "Installed to $bin (MCP points at ComputeWarden.Mcp.exe there)."
    return
}

Write-Host ""
Write-Host "Done. Point Claude Code at: $(Join-Path $out 'ComputeWarden.Mcp.exe')"
Write-Host "(Run with -Install to deploy to %LOCALAPPDATA%\ComputeWarden\bin.)"
