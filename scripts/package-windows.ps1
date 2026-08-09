param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Rid,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$publishDirectoryPath = (Resolve-Path $PublishDirectory).Path
$outputDirectoryPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$executablePath = Join-Path $publishDirectoryPath 'FeishuWikiExporter.exe'

if (-not (Test-Path $executablePath -PathType Leaf)) {
    throw "FeishuWikiExporter.exe is missing from $publishDirectoryPath."
}

$unexpectedFiles = @(
    Get-ChildItem $publishDirectoryPath -File |
        Where-Object {
            $_.FullName -ne $executablePath -and
            $_.Extension -ne '.pdb'
        }
)
if ($unexpectedFiles.Count -gt 0) {
    $fileNames = ($unexpectedFiles.Name -join ', ')
    throw "Windows single-file publish produced unexpected runtime files: $fileNames"
}

New-Item -ItemType Directory -Path $outputDirectoryPath -Force | Out-Null
$workDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("feishu-wiki-exporter-" + [guid]::NewGuid())
$packageName = "feishu-wiki-exporter-$Version-$Rid"
$packageDirectory = Join-Path $workDirectory $packageName
$archivePath = Join-Path $outputDirectoryPath "$packageName.zip"

try {
    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
    Copy-Item $executablePath $packageDirectory
    Copy-Item (Join-Path $projectRoot 'LICENSE') $packageDirectory
    Copy-Item (Join-Path $projectRoot 'NOTICE') $packageDirectory
    Copy-Item (Join-Path $projectRoot 'README.md') $packageDirectory
    Copy-Item (Join-Path $projectRoot 'src/FeishuExporter.Desktop/Assets/Fonts/OFL-1.1.txt') `
        (Join-Path $packageDirectory 'NotoSansSC-OFL-1.1.txt')

    if (Test-Path $archivePath) {
        Remove-Item $archivePath -Force
    }
    Compress-Archive -Path $packageDirectory -DestinationPath $archivePath -CompressionLevel Optimal

    if (-not (Test-Path $archivePath -PathType Leaf)) {
        throw "Failed to create $archivePath."
    }
}
finally {
    if (Test-Path $workDirectory) {
        Remove-Item $workDirectory -Recurse -Force
    }
}
