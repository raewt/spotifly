param(
  [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$sourceDir = (Resolve-Path (Join-Path $ProjectRoot '_xpui\unpacked')).Path
$destSpa = Join-Path $ProjectRoot 'Apps\xpui.spa'
$tempSpa = "$destSpa.tmp.zip"

if (Test-Path -LiteralPath $tempSpa) {
  Remove-Item -LiteralPath $tempSpa -Force
}

$fileStream = [System.IO.File]::Create($tempSpa)
try {
  $archive = [System.IO.Compression.ZipArchive]::new(
    $fileStream,
    [System.IO.Compression.ZipArchiveMode]::Create
  )
  try {
    Get-ChildItem -LiteralPath $sourceDir -Recurse -File | ForEach-Object {
      $relativePath = $_.FullName.Substring($sourceDir.Length).TrimStart('\', '/').Replace('\', '/')
      $entry = $archive.CreateEntry($relativePath, [System.IO.Compression.CompressionLevel]::Optimal)
      $entryStream = $entry.Open()
      try {
        $inputStream = [System.IO.File]::OpenRead($_.FullName)
        try {
          $inputStream.CopyTo($entryStream)
        }
        finally {
          $inputStream.Dispose()
        }
      }
      finally {
        $entryStream.Dispose()
      }
    }
  }
  finally {
    $archive.Dispose()
  }
}
finally {
  $fileStream.Dispose()
}

Move-Item -LiteralPath $tempSpa -Destination $destSpa -Force

$verification = [System.IO.Compression.ZipFile]::OpenRead($destSpa)
try {
  $themeEntry = $verification.GetEntry('spotifly/theme.js')
  if (-not $themeEntry) {
    throw 'spotifly/theme.js is missing from xpui.spa'
  }
  $reader = [System.IO.StreamReader]::new($themeEntry.Open())
  try {
    $packedTheme = $reader.ReadToEnd()
  }
  finally {
    $reader.Dispose()
  }

  if (-not $packedTheme.Contains('wallpaperVideoShouldPlay')) {
    throw 'The wallpaper lifecycle optimization is missing from packed xpui.spa'
  }
  if ($packedTheme.Contains('setAttribute("autoplay"')) {
    throw 'Wallpaper video still has autonomous autoplay enabled'
  }
  if (-not $packedTheme.Contains('openWallpaperBrowser') -or -not $packedTheme.Contains('/api/activity')) {
    throw 'Wallpaper Engine integration is missing from packed xpui.spa'
  }
  if (-not $packedTheme.Contains('/api/prepare/') -or -not $packedTheme.Contains('importLocalVideo') -or -not $packedTheme.Contains('wallpaperFit')) {
    throw 'Stable video preparation or layout controls are missing from packed xpui.spa'
  }
  if (@($verification.Entries | Where-Object { $_.FullName.Contains('\') }).Count -ne 0) {
    throw 'xpui.spa contains Windows path separators'
  }

  [pscustomobject]@{
    Path = $destSpa
    SizeMB = [math]::Round((Get-Item -LiteralPath $destSpa).Length / 1MB, 2)
    Entries = $verification.Entries.Count
    LifecycleOptimization = $true
    WallpaperEngineIntegration = $true
  }
}
finally {
  $verification.Dispose()
}
