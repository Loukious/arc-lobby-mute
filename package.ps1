$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$packageBuild = Join-Path $projectRoot 'build\package'
& (Join-Path $projectRoot 'build.ps1') -Test -OutputDirectory $packageBuild

$version = (Get-Content -LiteralPath (Join-Path $projectRoot 'VERSION') -Raw).Trim()
$releaseDir = Join-Path $projectRoot 'release'
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
$archivePath = Join-Path $releaseDir "ArcLobbyMute-v$version-win-x64.zip"
Compress-Archive -LiteralPath (Join-Path $packageBuild 'ArcLobbyMute.exe'), (Join-Path $packageBuild 'README.md') -DestinationPath $archivePath -Force
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([System.IO.Path]::GetFileName($archivePath))" | Set-Content -LiteralPath "$archivePath.sha256" -Encoding ASCII
Get-Item -LiteralPath $archivePath, "$archivePath.sha256" | Select-Object FullName, Length
