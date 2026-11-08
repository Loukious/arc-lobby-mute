param(
    [switch]$Test,
    [switch]$StateOnly,
    [string]$OutputDirectory = 'dist'
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x C# compiler was not found.' }
$outputDir = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectRoot $OutputDirectory }
$buildDir = Join-Path $projectRoot 'build'
New-Item -ItemType Directory -Path $outputDir, $buildDir -Force | Out-Null

$version = (Get-Content -LiteralPath (Join-Path $projectRoot 'VERSION') -Raw).Trim()
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'VERSION must contain a stable version such as 1.1.0.'
}
foreach ($part in $version.Split('.')) {
    if ([long]$part -gt 65534) { throw 'Each version component must be at most 65534.' }
}
$versionSource = Join-Path $buildDir 'AssemblyVersion.g.cs'
@"
using System.Reflection;
[assembly: AssemblyVersion("$version.0")]
[assembly: AssemblyFileVersion("$version.0")]
[assembly: AssemblyInformationalVersion("$version")]
"@ | Set-Content -LiteralPath $versionSource -Encoding UTF8
$manifestPath = Join-Path $buildDir 'app.manifest'
$manifest = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'app.manifest') -Raw)
$manifest.assembly.assemblyIdentity.version = "$version.0"
$manifest.Save($manifestPath)

# Draw the icon locally; no downloads or package dependencies.
Add-Type -AssemblyName System.Drawing
$bitmap = [System.Drawing.Bitmap]::new(32, 32)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::FromArgb(26, 34, 45))
$brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(68, 220, 185))
$points = [System.Drawing.Point[]]@(
    [System.Drawing.Point]::new(6, 12), [System.Drawing.Point]::new(11, 12),
    [System.Drawing.Point]::new(18, 6), [System.Drawing.Point]::new(18, 26),
    [System.Drawing.Point]::new(11, 20), [System.Drawing.Point]::new(6, 20))
$graphics.FillPolygon($brush, $points)
$pen = [System.Drawing.Pen]::new($brush.Color, 2)
$graphics.DrawArc($pen, 17, 7, 11, 18, -60, 120)
$imageStream = [System.IO.MemoryStream]::new()
$bitmap.Save($imageStream, [System.Drawing.Imaging.ImageFormat]::Png)
$imageBytes = $imageStream.ToArray()
$iconPath = Join-Path $buildDir 'app.ico'
$file = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
    $writer.Write([byte]32); $writer.Write([byte]32); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$imageBytes.Length); $writer.Write([uint32]22); $writer.Write($imageBytes)
} finally {
    $writer.Dispose(); $file.Dispose(); $imageStream.Dispose()
    $pen.Dispose(); $brush.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}

$common = @('/nologo', '/optimize+', '/platform:x64', '/langversion:5', '/warnaserror+',
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll')
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$sources += $versionSource
& $compiler @common /target:winexe "/out:$outputDir\ArcLobbyMute.exe" "/win32icon:$iconPath" "/win32manifest:$manifestPath" @sources
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $outputDir 'README.md') -Force
Get-Item -LiteralPath (Join-Path $outputDir 'ArcLobbyMute.exe') | Select-Object FullName, Length

if ($Test -or $StateOnly) {
    $testSources = @((Join-Path $projectRoot 'src\GameState.cs'), (Join-Path $projectRoot 'src\CoreAudio.cs'), (Join-Path $projectRoot 'tests\Tests.cs'))
    & $compiler @common /target:exe "/out:$buildDir\ArcLobbyMute.Tests.exe" @testSources
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    $testArguments = if ($StateOnly) { @('--state-only') } else { @() }
    & (Join-Path $buildDir 'ArcLobbyMute.Tests.exe') @testArguments
    if ($LASTEXITCODE -ne 0) { throw 'Verification failed.' }
}
