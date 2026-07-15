# Build Claude Meter.exe from Program.cs. Usage:
#   ./build.ps1             build only (windows/build/Claude Meter.exe)
#   ./build.ps1 -Install    build, install to %LOCALAPPDATA%\Programs, launch
#   ./build.ps1 -Portable   self-contained single exe (no .NET runtime needed
#                           on the target machine; much bigger file).
#                           Targets this machine's architecture unless
#                           overridden, e.g. -Portable -Arch x64
param([switch]$Install, [switch]$Portable, [ValidateSet("x64", "arm64")][string]$Arch)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

Write-Host "Compiling…"
if ($Portable) {
    if (-not $Arch) { $Arch = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "arm64" } else { "x64" } }
    dotnet publish -c Release -r "win-$Arch" --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o build
} else {
    dotnet publish -c Release -o build
}
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Write-Host "Built build\Claude Meter.exe"

if ($Install) {
    $dest = "$env:LOCALAPPDATA\Programs\Claude Meter"
    # Quit a running copy so the binary can be replaced cleanly.
    Stop-Process -Name "Claude Meter" -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item build\* $dest -Recurse -Force
    Write-Host "Installed to $dest — launching…"
    Start-Process "$dest\Claude Meter.exe"
}
