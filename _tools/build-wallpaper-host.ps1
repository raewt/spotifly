param(
  [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $ProjectRoot 'wallpaper-host\Spotifly.WallpaperHost.csproj'
$publishDir = Join-Path $ProjectRoot 'wallpaper-host\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish'

dotnet publish $project -c Release
if ($LASTEXITCODE -ne 0) {
  throw 'Wallpaper Host publish failed'
}

$executable = Join-Path $publishDir 'Spotifly.WallpaperHost.exe'
if (-not (Test-Path -LiteralPath $executable)) {
  throw 'Spotifly.WallpaperHost.exe was not produced'
}

[pscustomobject]@{
  Path = $executable
  SizeMB = [math]::Round((Get-Item -LiteralPath $executable).Length / 1MB, 2)
  SelfContained = $true
}
