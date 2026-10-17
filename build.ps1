param(
    [ValidateSet('Portable', 'Lite')][string]$Edition = 'Portable'
)
$ErrorActionPreference = 'Stop'
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet/dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'Install .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0' }
Push-Location $PSScriptRoot
try {
    & $dotnet run --project tests/MangaDexLite.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
    $bundled = if ($Edition -eq 'Portable') { 'true' } else { 'false' }
    # A fresh staging directory prevents files from older SDK/edition builds leaking into ZIPs.
    $output = Join-Path $PSScriptRoot ('artifacts/' + $Edition + '-' + [guid]::NewGuid().ToString('N'))
    & $dotnet publish src/MangaDexLite/MangaDexLite.csproj -c Release -p:Platform=x64 `
        -p:SelfContained=$bundled -p:WindowsAppSDKSelfContained=$bundled -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    if (-not (Test-Path -LiteralPath (Join-Path $output 'MangaDexLite.pri'))) {
        throw 'Publish is missing the application resource index; refusing to package a broken app'
    }
    Copy-Item -LiteralPath README.md -Destination (Join-Path $output 'README.md')
    Copy-Item -LiteralPath LICENSE -Destination (Join-Path $output 'LICENSE.txt')
    $assets = Get-Content src/MangaDexLite/obj/project.assets.json -Raw | ConvertFrom-Json
    $packageRoot = $assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1
    $licenseSources = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object { Join-Path $packageRoot $_.Value.path })
    if ($Edition -eq 'Portable') {
        $runtimeConfig = Get-Content (Join-Path $output 'MangaDexLite.runtimeconfig.json') -Raw | ConvertFrom-Json
        $runtimeVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
        $licenseSources += Join-Path $packageRoot "microsoft.netcore.app.runtime.win-x64/$runtimeVersion"
    }
    foreach ($source in $licenseSources) {
        foreach ($name in @('LICENSE.txt', 'NOTICE.txt', 'THIRD-PARTY-NOTICES.txt')) {
            $notice = Join-Path $source $name
            if (Test-Path -LiteralPath $notice) {
                $destination = Join-Path $output ('licenses/' + (Split-Path (Split-Path $source) -Leaf) + '-' + (Split-Path $source -Leaf))
                $null = New-Item -ItemType Directory -Path $destination -Force
                Copy-Item -LiteralPath $notice -Destination (Join-Path $destination $name)
            }
        }
    }
    $zip = Join-Path $PSScriptRoot "artifacts/MangaDexLite-$Edition-win-x64.zip"
    Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip -Force
    $size = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)
    Write-Output "$zip ($size MiB)"
} finally { Pop-Location }
