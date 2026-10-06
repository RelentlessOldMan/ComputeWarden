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

    # Clean up exes moved aside by earlier installs, once no session is still running them.
    Get-ChildItem $bin -Filter "*.old-*" -File -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }

    # The adapter exe stays locked while any Claude/Codex session is connected (user-scope MCP
    # spawns it per session). Windows won't overwrite a running exe but will rename it, so move
    # the old one aside and copy the new one in: open sessions keep running the old copy, new
    # sessions get the new one, and the leftover is deleted by a later install.
    $locked = @()
    $movedAside = @()
    Get-ChildItem $out -File | ForEach-Object {
        # Capture before any try/catch: inside a catch block $_ is the error, not the file.
        $name = $_.Name
        $source = $_.FullName
        $dest = Join-Path $bin $name
        try { Copy-Item $source $dest -Force -ErrorAction Stop }
        catch {
            $aside = "$name.old-$(Get-Date -Format yyyyMMddHHmmss)"
            try { Rename-Item $dest $aside -ErrorAction Stop }
            catch { $locked += $name; return }

            # If the copy fails, put the old file back: never leave the registered path empty.
            try {
                Copy-Item $source $dest -Force -ErrorAction Stop
                $movedAside += $name
            }
            catch {
                Rename-Item (Join-Path $bin $aside) $name -ErrorAction SilentlyContinue
                $locked += $name
            }
        }
    }
    Write-Host ""
    if ($movedAside.Count -gt 0) {
        Write-Host "Updated while in use (old copy moved aside): $($movedAside -join ', ')"
        Write-Host "Restart Claude/Codex sessions to pick up the new adapter."
    }
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
