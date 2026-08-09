$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $projectRoot 'artifacts'
$version = (Get-Content (Join-Path $projectRoot 'VERSION') -Raw).Trim()
$rids = @(
    'win-x64', 'win-arm64',
    'linux-x64', 'linux-arm64',
    'linux-musl-x64', 'linux-musl-arm64'
)

foreach ($rid in $rids) {
    $publishDirectory = Join-Path $outputRoot "feishu-wiki-exporter-$version-$rid"
    if (Test-Path $publishDirectory) {
        Remove-Item $publishDirectory -Recurse -Force
    }
    dotnet publish (Join-Path $projectRoot 'src/FeishuExporter.Desktop/FeishuExporter.Desktop.csproj') `
        -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false -o $publishDirectory

    Copy-Item (Join-Path $projectRoot 'LICENSE') $publishDirectory
    Copy-Item (Join-Path $projectRoot 'NOTICE') $publishDirectory
    Copy-Item (Join-Path $projectRoot 'README.md') $publishDirectory
    Copy-Item (Join-Path $projectRoot 'src/FeishuExporter.Desktop/Assets/Fonts/OFL-1.1.txt') `
        (Join-Path $publishDirectory 'NotoSansSC-OFL-1.1.txt')
}

Write-Host "Published to $outputRoot"
