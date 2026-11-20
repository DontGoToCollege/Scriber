# Publishes Scriber as a self-contained, single-file x64 executable.
# Usage:  .\publish.ps1            -> Scriber.exe (and Versions\Archive\Scriber-<version>.exe)
#         .\publish.ps1 -Run       -> publish and launch
# Refuses a version that's already in Versions\Archive: raise <Version> in the csproj first (the only
# place the version is set). The build must pass its own self-check (--smoke) before it is kept.
# If Scriber is running, it is closed first and started again on the new version.
param([switch]$Run)

$ErrorActionPreference = "Stop"
$name = "Scriber"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "src\$name\$name.csproj"
$out  = Join-Path $root "dist"
$top  = Join-Path $root "$name.exe"

$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$archive = Join-Path $root "Versions\Archive\$name-$version.exe"
if (Test-Path $archive) { throw "Version $version is already archived. Raise <Version> in the csproj, then publish again." }

# Ask a running copy to close; never kill it.
$running = @(Get-Process $name -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $top })
foreach ($p in $running) {
    [void]$p.CloseMainWindow()
    if (-not $p.WaitForExit(15000)) { throw "Scriber is still open. Close it, then publish again." }
}

# On some PCs the `dotnet` on PATH is the x86 one without an SDK.
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

Write-Host "Publishing Scriber $version (Release, win-x64, self-contained, single file)..." -ForegroundColor Cyan
& $dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
$exe = Join-Path $out "$name.exe"

Write-Host "Self-check..." -ForegroundColor Cyan
$data = Join-Path $out "$name-Data"
if (Test-Path $data) { Remove-Item $data -Recurse -Force }
Start-Process $exe "--smoke" -Wait
$report = Get-Content (Join-Path $data "smoke.txt") -Raw
if ($report -notmatch "^PASS") { throw "self-check failed:`n$report" }
Write-Host $report

# Only the exe is needed; drop symbol/side files and the self-check's data.
Get-ChildItem $out | Where-Object { $_.Name -ne "$name.exe" } | Remove-Item -Recurse -Force

# Every build is kept: the current one at the top level, and a copy per version in Versions\Archive.
New-Item -ItemType Directory -Force (Split-Path $archive) | Out-Null
Copy-Item $exe $archive
Copy-Item $exe $top
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "Done: $top ($size MB), archived as $archive" -ForegroundColor Green

# Started through Windows so it outlives this script.
if ($Run -or $running) { [void](Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$top`"" }) }
