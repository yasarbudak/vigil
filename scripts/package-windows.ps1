<#
.SYNOPSIS
  Builds Native AOT releases of vigil.exe, vigild.exe and vigil-tray.exe and zips them under artifacts/<rid>/.

.DESCRIPTION
  Usage: scripts/package-windows.ps1 win-x64|win-arm64

  vigil.exe is the console program (every command). vigild.exe is the windowless daemon that `vigil install` puts
  in the per-user Run key; it is built from the same code (src/Vigild, OutputType WinExe). vigil-tray.exe is the
  notification area icon and settings window (src/Vigil.Tray, OutputType WinExe); it needs the native libraries Avalonia
  ships (Skia, HarfBuzz, ANGLE), so it lives in a folder of its own, tray, next to the others. Output:
    artifacts/<rid>/vigil.exe
    artifacts/<rid>/vigild.exe
    artifacts/<rid>/tray/vigil-tray.exe and its .dll files
    artifacts/<rid>/Vigil-<version>-<rid>.zip   (a folder named Vigil holding the programs, the tray folder, the LICENSE file and THIRD-PARTY-NOTICES.txt)

  Native AOT cannot compile across operating systems, so this runs on Windows. Cross-architecture works on Windows
  (https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile): win-arm64 can be built on an
  x64 machine if the Visual Studio component "C++ ARM64/ARM64EC build tools" is installed, and the other way round
  with the x64/x86 tools. A binary built for the other architecture cannot be run here.

  Nothing is signed: the programs are unsigned, so SmartScreen may warn on first run.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true, Position = 0)]
  [ValidateSet('win-x64', 'win-arm64')]
  [string]$Rid
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
  throw 'package-windows.ps1 must run on Windows (Native AOT cannot cross-compile between operating systems).'
}

$Root = Split-Path -Parent $PSScriptRoot
$Out = Join-Path (Join-Path $Root 'artifacts') $Rid
$PublishDir = Join-Path $Out 'publish'
$Programs = @(
  @{ Project = 'src/Vigil/Vigil.csproj'; File = 'vigil.exe'; Subsystem = 3; SubsystemName = 'console' },
  @{ Project = 'src/Vigild/Vigild.csproj'; File = 'vigild.exe'; Subsystem = 2; SubsystemName = 'windows (no console window)' }
)
$TrayProject = 'src/Vigil.Tray/Vigil.Tray.csproj'
$TrayExe = 'vigil-tray.exe'
$TrayOut = Join-Path $Out 'tray'

# The Subsystem field of the PE optional header decides whether Windows opens a console window: 2 = windows GUI,
# 3 = console. Its offset is 68 in the optional header of both PE32 and PE32+; the header starts after the 4-byte
# "PE" signature and the 20-byte file header, and its position is stored at 0x3C.
# https://learn.microsoft.com/en-us/windows/win32/debug/pe-format
function Get-PeSubsystem([string]$Path) {
  $bytes = [System.IO.File]::ReadAllBytes($Path)
  $pe = [System.BitConverter]::ToInt32($bytes, 0x3C)
  if ([System.BitConverter]::ToUInt32($bytes, $pe) -ne 0x00004550) {
    throw "$Path is not a PE file."
  }
  return [System.BitConverter]::ToUInt16($bytes, $pe + 24 + 68)
}

$Version = (& dotnet msbuild (Join-Path $Root 'src/Vigil/Vigil.csproj') -getProperty:Version -nologo | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $Version -notmatch '^\d+(\.\d+){1,3}$') {
  throw "unexpected project version '$Version'"
}

Write-Host "Publishing Vigil $Version for $Rid"

# Only remove what this script created, and only below artifacts/.
$ArtifactsPrefix = (Join-Path $Root 'artifacts') + [System.IO.Path]::DirectorySeparatorChar
if (-not $Out.StartsWith($ArtifactsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
  throw "refusing to clean $Out"
}
foreach ($stale in @($PublishDir, $TrayOut)) {
  if (Test-Path -LiteralPath $stale) {
    Remove-Item -LiteralPath $stale -Recurse -Force
  }
}
New-Item -ItemType Directory -Force -Path $Out | Out-Null
foreach ($stale in @('vigil.exe', 'vigild.exe') + @(Get-ChildItem -LiteralPath $Out -Filter 'Vigil-*.zip' -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })) {
  Remove-Item -LiteralPath (Join-Path $Out $stale) -Force -ErrorAction SilentlyContinue
}

foreach ($program in $Programs) {
  $target = Join-Path $PublishDir ([System.IO.Path]::GetFileNameWithoutExtension($program.File))
  & dotnet publish (Join-Path $Root $program.Project) -c Release -r $Rid -o $target --nologo
  if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish of $($program.Project) failed with exit code $LASTEXITCODE"
  }

  $built = Join-Path $target $program.File
  if (-not (Test-Path -LiteralPath $built)) {
    throw "the publish did not produce $built"
  }
  if ((Get-PeSubsystem $built) -ne $program.Subsystem) {
    throw "$($program.File) has the wrong PE subsystem: expected $($program.SubsystemName)."
  }
  Copy-Item -LiteralPath $built -Destination (Join-Path $Out $program.File)
}

# The tray comes with Avalonia's native libraries (the .dll files next to it), which it loads from its own folder. The debug
# symbols stay behind. The publish output of a WinExe holds nothing else the program needs.
$trayTarget = Join-Path $PublishDir 'vigil-tray'
& dotnet publish (Join-Path $Root $TrayProject) -c Release -r $Rid -o $trayTarget --nologo
if ($LASTEXITCODE -ne 0) {
  throw "dotnet publish of $TrayProject failed with exit code $LASTEXITCODE"
}
$trayBuilt = Join-Path $trayTarget $TrayExe
if (-not (Test-Path -LiteralPath $trayBuilt)) {
  throw "the publish did not produce $trayBuilt"
}
if ((Get-PeSubsystem $trayBuilt) -ne 2) {
  throw "$TrayExe has the wrong PE subsystem: expected windows (no console window)."
}
New-Item -ItemType Directory -Force -Path $TrayOut | Out-Null
$trayFiles = @(Get-ChildItem -LiteralPath $trayTarget -File | Where-Object { $_.Extension -ne '.pdb' })
foreach ($file in $trayFiles) {
  Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $TrayOut $file.Name)
}
foreach ($required in @($TrayExe, 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll')) {
  if (-not (Test-Path -LiteralPath (Join-Path $TrayOut $required))) {
    throw "the tray folder is missing $required"
  }
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$License = Join-Path $Root 'LICENSE'
$Notices = Join-Path $Root 'THIRD-PARTY-NOTICES.txt'
foreach ($required in @($License, $Notices)) {
  if (-not (Test-Path -LiteralPath $required)) {
    throw "$required is missing (the licenses of Vigil and of the components it ships require their notices in every distributed copy)"
  }
}
$Zip = Join-Path $Out "Vigil-$Version-$Rid.zip"
$archive = [System.IO.Compression.ZipFile]::Open($Zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
  foreach ($program in $Programs) {
    # Entry names use forward slashes, as the zip format wants.
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
      $archive, (Join-Path $Out $program.File), "Vigil/$($program.File)", [System.IO.Compression.CompressionLevel]::Optimal)
  }
  foreach ($file in $trayFiles) {
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
      $archive, (Join-Path $TrayOut $file.Name), "Vigil/tray/$($file.Name)", [System.IO.Compression.CompressionLevel]::Optimal)
  }
  # The MIT license requires its notice in every distributed copy: it goes next to vigil.exe.
  [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
    $archive, $License, 'Vigil/LICENSE', [System.IO.Compression.CompressionLevel]::Optimal)
  # The licenses of the third-party components in the programs and .dll files (Avalonia, Skia, HarfBuzz, ANGLE, the .NET runtime).
  [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
    $archive, $Notices, 'Vigil/THIRD-PARTY-NOTICES.txt', [System.IO.Compression.CompressionLevel]::Optimal)
}
finally {
  $archive.Dispose()
}

Get-ChildItem -LiteralPath $Out -File | ForEach-Object { Write-Host ('{0,12:N0}  {1}' -f $_.Length, $_.Name) }
Get-ChildItem -LiteralPath $TrayOut -File | ForEach-Object { Write-Host ('{0,12:N0}  tray/{1}' -f $_.Length, $_.Name) }
Write-Host "Built $Zip"
