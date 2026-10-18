# Cuts a ComputeWarden release: runs tests, publishes self-contained win-x64 exes, zips them
# with the docs/config, bumps the version, tags, pushes, and creates a GitHub Release.
#
#   ./release.ps1                # first run releases the current version; later runs bump patch (1.0.x)
#   ./release.ps1 -Version 1.1.0 # release a specific version
#   ./release.ps1 -DryRun        # build + zip only; no version bump, tag, push, or release
param(
    [string]$Version,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$propsPath = Join-Path $root "Directory.Build.props"

function Read-Version {
    $text = Get-Content $propsPath -Raw
    return ([regex]::Match($text, '<Version>([^<]*)</Version>')).Groups[1].Value
}

# --- Preconditions -------------------------------------------------------
$branch = (git -C $root rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne "main") { throw "Releases are cut from 'main' (currently on '$branch')." }
if ((git -C $root status --porcelain)) { throw "Working tree has uncommitted changes; commit or stash first." }
# The GitHub repo this checkout pushes to, e.g. owner/ComputeWarden.
$repo = (gh repo view --json nameWithOwner --jq .nameWithOwner 2>$null)
if (-not $DryRun -and -not $repo) { throw "Couldn't tell which GitHub repo this is (gh repo view failed); is gh signed in?" }

# --- Decide the version --------------------------------------------------
$current = Read-Version
$tags = git -C $root tag
if (-not $Version) {
    if (-not $tags) {
        $Version = $current                              # first release: ship what's in the props
    } else {
        $p = $current.Split('.')
        $Version = "$($p[0]).$($p[1]).$([int]$p[2] + 1)" # otherwise bump the patch (1.0.x)
    }
}
if (git -C $root tag --list "v$Version") { throw "Tag v$Version already exists." }
Write-Host "Releasing v$Version (current props: $current)"

# --- Tests ---------------------------------------------------------------
Write-Host "Running tests..."
dotnet test (Join-Path $root "tests\ComputeWarden.Tests\ComputeWarden.Tests.csproj") --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw "Tests failed; aborting release." }

# --- Bump version (so the built exes carry it) ---------------------------
$propsBefore = Get-Content $propsPath -Raw
if ($Version -ne $current) {
    $propsBefore -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>" |
        Set-Content $propsPath -NoNewline -Encoding utf8
    Write-Host "Bumped Directory.Build.props to $Version"
}

# --- Publish + zip -------------------------------------------------------
try {
    & (Join-Path $root "publish.ps1")
    if ($LASTEXITCODE -ne 0) { throw "publish.ps1 failed." }

    # Same layout as the repo, so the README's relative links (DESIGN.md, examples/CLAUDE.md) work.
    $pub = Join-Path $root "publish"
    $stage = Join-Path ([IO.Path]::GetTempPath()) "ComputeWarden-v$Version"
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force (Join-Path $stage "examples") | Out-Null
    foreach ($exe in "ComputeWarden.Daemon.exe", "ComputeWarden.Mcp.exe", "cw.exe") {
        Copy-Item (Join-Path $pub $exe) $stage
    }
    foreach ($doc in "config.example.yaml", ".mcp.json.example", "README.md", "DESIGN.md", "LICENSE", "examples\CLAUDE.md") {
        Copy-Item (Join-Path $root $doc) (Join-Path $stage $doc)
    }
    $zip = Join-Path $root "ComputeWarden-v$Version-win-x64.zip"
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -Force
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Packaged $zip"
}
finally {
    # A dry run, or a failed build, leaves the version as it was.
    if ($DryRun -or -not $zip) {
        Set-Content $propsPath $propsBefore -NoNewline -Encoding utf8
    }
}

if ($DryRun) {
    Write-Host "DryRun: skipping commit, tag, push, and GitHub release."
    return
}

# --- Commit (if bumped), tag, push, release ------------------------------
# git/gh write benign status to stderr ("Everything up-to-date"), which PowerShell 5.1 would
# treat as terminating under -ErrorActionPreference Stop. Switch to explicit exit-code checks.
$ErrorActionPreference = "Continue"

if ($Version -ne $current) {
    git -C $root add Directory.Build.props
    git -C $root commit -m "Release v$Version"
    if ($LASTEXITCODE -ne 0) { throw "git commit failed." }
}
git -C $root tag "v$Version"
if ($LASTEXITCODE -ne 0) { throw "git tag failed." }
git -C $root push
if ($LASTEXITCODE -ne 0) { throw "git push failed." }
git -C $root push origin "v$Version"
if ($LASTEXITCODE -ne 0) { throw "git tag push failed." }

gh release create "v$Version" $zip --repo $repo --title "ComputeWarden v$Version" --generate-notes
if ($LASTEXITCODE -ne 0) {
    # The tag is already pushed, so re-running this script would stop at "tag exists". Finish by hand.
    throw "gh release failed. The tag v$Version is pushed; finish with: gh release create v$Version `"$zip`" --repo $repo --title `"ComputeWarden v$Version`" --generate-notes"
}
Write-Host ""
Write-Host "Released v$Version -> https://github.com/$repo/releases/tag/v$Version"
