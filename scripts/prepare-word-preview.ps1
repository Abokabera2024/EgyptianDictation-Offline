param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $projectRoot '.dotnet\dotnet.exe'
$preview = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else {
    Join-Path $projectRoot 'artifacts\word-preview'
}
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.packages\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

$app = Join-Path $preview 'app'
$worker = Join-Path $preview 'runtime\cohere'
$addin = Join-Path $preview 'addin'
New-Item -ItemType Directory -Force -Path $app, $worker, $addin | Out-Null

& $dotnet publish (Join-Path $projectRoot 'src\EgyptianDictation.Host\EgyptianDictation.Host.csproj') `
    -c Release -r win-x64 --self-contained true -o $app -p:PublishReadyToRun=false
if ($LASTEXITCODE -ne 0) { throw 'Preview host publish failed.' }
& $dotnet publish (Join-Path $projectRoot 'src\ArabicSTTWorker\ArabicSTTWorker.csproj') `
    -c Release -r win-x64 --self-contained true -o $worker -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw 'Preview worker publish failed.' }
& $dotnet build (Join-Path $projectRoot 'src\EgyptianDictation.WordAddIn\EgyptianDictation.WordAddIn.csproj') `
    -c Release -p:PreviewAddIn=true
if ($LASTEXITCODE -ne 0) { throw 'Preview Word add-in build failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\EgyptianDictation.WordAddIn\bin\Release\net48\EgyptianDictation.WordAddIn.Preview.dll'), `
    (Join-Path $projectRoot 'src\EgyptianDictation.Contracts\bin\Release\netstandard2.0\EgyptianDictation.Contracts.dll') -Destination $addin -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'config\engine-settings.json') -Destination $app -Force

foreach ($required in @(
    (Join-Path $app 'EgyptianDictation.Host.exe'),
    (Join-Path $worker 'ArabicSTTWorker.exe'),
    (Join-Path $worker 'transcribe.dll'),
    (Join-Path $worker 'ggml-vulkan.dll'),
    (Join-Path $addin 'EgyptianDictation.WordAddIn.Preview.dll')
)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Preview file missing: $required" }
}
Write-Output "Word preview ready: $preview"
