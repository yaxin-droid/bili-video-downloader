param(
    [Parameter(Mandatory = $true)] [string] $ToolsDirectory,
    [string] $OutputPath = (Join-Path $PSScriptRoot 'B站视频下载器-Windows-x64-v1.1.0.zip')
)

$ErrorActionPreference = 'Stop'
$tools = (Resolve-Path -LiteralPath $ToolsDirectory).Path
$OutputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$vendorFiles = @('yt-dlp.exe', 'ffmpeg.exe', 'ffprobe.exe', 'yt-dlp-LICENSE.txt', 'FFmpeg-LICENSE.txt', 'versions.txt')
foreach ($file in $vendorFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $tools $file) -PathType Leaf)) {
        throw "Missing component file $file in $tools"
    }
}

$buildId = [Guid]::NewGuid().ToString('N')
$app = Join-Path ([IO.Path]::GetTempPath()) "BiliVideoDownloader-$buildId.exe"
$archivePath = Join-Path ([IO.Path]::GetDirectoryName($OutputPath)) ".BiliVideoDownloader-$buildId.zip"

try {
    $sources = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName
    $manifest = Join-Path $PSScriptRoot 'src\app.manifest'
    & $compiler /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /warnaserror+ `
        /r:System.Net.Http.dll /r:System.Web.Extensions.dll "/win32manifest:$manifest" "/out:$app" @sources
    if ($LASTEXITCODE -ne 0) { throw "C# compiler exited with $LASTEXITCODE" }

    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $files = [ordered]@{
            'B站视频下载器/B站视频下载器.exe' = $app
            'B站视频下载器/使用说明.txt' = (Join-Path $PSScriptRoot 'usage.txt')
        }
        foreach ($file in $vendorFiles) { $files["B站视频下载器/tools/$file"] = Join-Path $tools $file }
        foreach ($entry in $files.GetEnumerator()) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $entry.Value, $entry.Key,
                [IO.Compression.CompressionLevel]::Fastest) | Out-Null
        }
    } finally {
        $archive.Dispose()
    }

    Move-Item -LiteralPath $archivePath -Destination $OutputPath -Force
    Get-Item -LiteralPath $OutputPath
} finally {
    foreach ($file in @($app, $archivePath)) {
        if (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file }
    }
}
