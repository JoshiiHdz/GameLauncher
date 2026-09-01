<#
Builds a portable, self-contained single-file GameLauncher.exe that runs on any
Windows PC without installing the .NET runtime. Copy the output folder anywhere
(USB stick, another PC, etc.) - settings and icon cache live next to the exe.
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutDir = "dist"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csproj = Join-Path $root "src\GameLauncher\GameLauncher.csproj"
$outPath = Join-Path $root $OutDir

dotnet publish $csproj `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishReadyToRun=false `
    -o $outPath

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host ""
Write-Host "Portable build ready: $outPath\GameLauncher.exe"
Write-Host "Copy the '$OutDir' folder anywhere and run GameLauncher.exe - no install needed."
