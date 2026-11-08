<#
.SYNOPSIS
  Builds, tests and publishes hdlc-loopback as a SINGLE self-contained executable.

.DESCRIPTION
  One .exe with no .dll beside it and no .NET install needed on the target machine. The
  switches that achieve that, because each one matters:

    PublishSingleFile                    packs the managed assemblies into the exe
    IncludeNativeLibrariesForSelfExtract native libraries too (System.IO.Ports has one)
    SatelliteResourceLanguages=en        no per-language resource subdirectories
    DebugType=none                       no .pdb beside the exe
    EnableCompressionInSingleFile        roughly halves the size; self-contained only

  The tests run first and a failure stops the publish.

.PARAMETER Configuration
  Release (default) or Debug.

.PARAMETER Runtime
  RID to publish for. Default win-x64; linux-x64 and osx-arm64 also work.

.PARAMETER Output
  Where the exe goes. Default: publish\<Runtime> next to this script.

.PARAMETER Version
  The version written into the executable, for example 1.2.3 or 1.2.3-rc.1. The release
  workflow passes the tag without its leading v.

.PARAMETER SkipTests
  Do not run the unit tests first.

.PARAMETER Clean
  Delete the output directory first.

.EXAMPLE
  .\build.ps1
  .\build.ps1 -Version 1.2.3
#>
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Output = '',
    [string]$Version = '',
    [switch]$SkipTests,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$') {
    throw "-Version '$Version' is not a version like 1.2.3 or 1.2.3-rc.1"
}
if (-not $Output) { $Output = Join-Path $root (Join-Path 'publish' $Runtime) }

# nodeReuse:false on every call. Node reuse is ON by default and leaves MSBuild worker
# processes behind; each one holds a DLL open, so the next build silently keeps the old
# binary and the tests pass against stale code.
$common = @('-nodeReuse:false')
if ($Version) { $common += "-p:Version=$Version" }

if ($Clean -and (Test-Path $Output)) {
    Write-Host "cleaning $Output"
    Remove-Item $Output -Recurse -Force
}

Write-Host '== Restore and build'
dotnet build -c $Configuration @common
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

if (-not $SkipTests) {
    Write-Host '== Test'
    dotnet test -c $Configuration --no-build @common
    if ($LASTEXITCODE -ne 0) { throw 'tests failed - nothing published.' }
}

Write-Host '== Publish hdlc-loopback'
$publishArgs = @(
    '-c', $Configuration
    '-r', $Runtime
    '--self-contained', 'true'
    '-p:PublishSingleFile=true'
    '-p:IncludeNativeLibrariesForSelfExtract=true'
    '-p:SatelliteResourceLanguages=en'
    '-p:DebugType=none'
    '-p:GenerateDocumentationFile=false'
    '-p:EnableCompressionInSingleFile=true'
)
New-Item -ItemType Directory -Force $Output | Out-Null
dotnet publish (Join-Path $root 'HdlcGatewayTest.Loopback\HdlcGatewayTest.Loopback.csproj') @publishArgs -o $Output @common
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

Write-Host ''
Get-ChildItem $Output -File | ForEach-Object {
    Write-Host ("  {0,-22} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB))
}
$loose = Get-ChildItem $Output -Recurse -File | Where-Object { $_.Extension -in '.dll', '.pdb' }
if ($loose) {
    Write-Host "  WARNING: $($loose.Count) loose .dll/.pdb file(s); the exe is not standalone." -ForegroundColor Yellow
} else {
    Write-Host '  No loose .dll or .pdb - the exe is standalone.' -ForegroundColor Green
}
Write-Host "  Output: $Output"
