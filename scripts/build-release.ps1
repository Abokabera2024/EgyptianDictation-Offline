param(
    [string]$Version = "2.5.0",
    [switch]$IncludeModel
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root ".dotnet\dotnet.exe"
$staging = Join-Path $root "artifacts\staging-cohere"
$release = Join-Path $root "artifacts\release"
$installer = Join-Path $root "installer"
$native = Join-Path $root "NativeSTT\runtime\transcribe-native-windows-x86_64-cpu-vulkan"
$modelName = "cohere-transcribe-arabic-07-2026-Q5_K_M.gguf"
$model = @((Join-Path $root $modelName),(Join-Path $root "models\$modelName")) |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$prerequisiteDirectory = Join-Path $root "prerequisites"
$prerequisiteManifest = Get-Content -LiteralPath (Join-Path $prerequisiteDirectory "manifest.json") -Raw | ConvertFrom-Json
$releaseLabel = "v$(([Version]$Version).ToString(2))"

$env:DOTNET_CLI_HOME = Join-Path $root ".tools\dotnet-home"
$env:NUGET_PACKAGES = Join-Path $root ".packages\nuget"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

if (-not (Test-Path -LiteralPath $dotnet)) { throw "Local .NET SDK is missing." }
if (-not (Test-Path -LiteralPath (Join-Path $native "transcribe.dll"))) { throw "Pinned transcribe.cpp v0.1.1 runtime is missing." }
if ($IncludeModel -and -not $model) { throw "The selected Cohere Q5_K_M model is missing." }
foreach ($package in @($prerequisiteManifest.visualCppX64, $prerequisiteManifest.netFramework48)) {
    $path = Join-Path $prerequisiteDirectory $package.fileName
    if (-not (Test-Path -LiteralPath $path)) { throw "Offline Microsoft prerequisite is missing: $path" }
    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($actualHash -ne $package.sha256) { throw "Prerequisite checksum mismatch: $path" }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*O=Microsoft Corporation*') {
        throw "Prerequisite is not validly signed by Microsoft: $path"
    }
}

& $dotnet build (Join-Path $root "src\EgyptianDictation.WordAddIn\EgyptianDictation.WordAddIn.csproj") -c Release -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "Word add-in build failed." }
& $dotnet build (Join-Path $root "src\ArabicSTTWorker\ArabicSTTWorker.csproj") -c Release -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "Native worker build failed." }
& $dotnet test (Join-Path $root "tests\EgyptianDictation.Tests\EgyptianDictation.Tests.csproj") -c Release --filter "FullyQualifiedName!~HostProcessTests&Category!=RealModel&Category!=RealModelGpu"
if ($LASTEXITCODE -ne 0) { throw "Tests failed." }

$expectedStaging = [IO.Path]::GetFullPath((Join-Path $root "artifacts\staging-cohere"))
if ([IO.Path]::GetFullPath($staging) -ne $expectedStaging -or
    -not $expectedStaging.StartsWith([IO.Path]::GetFullPath($root) + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe staging directory: $staging"
}
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path `
    (Join-Path $staging "app"), `
    (Join-Path $staging "addin"), `
    (Join-Path $staging "runtime\cohere"), `
    (Join-Path $staging "config"), `
    (Join-Path $staging "prerequisites"), `
    (Join-Path $staging "licenses"), `
    $release, $installer | Out-Null

& $dotnet publish (Join-Path $root "src\EgyptianDictation.Host\EgyptianDictation.Host.csproj") `
    -c Release -r win-x64 --self-contained true -o (Join-Path $staging "app") -p:PublishReadyToRun=false -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "Host publish failed." }
& $dotnet publish (Join-Path $root "src\ArabicSTTWorker\ArabicSTTWorker.csproj") `
    -c Release -r win-x64 --self-contained true -o (Join-Path $staging "runtime\cohere") -p:PublishSingleFile=false -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw "Worker publish failed." }

Copy-Item -LiteralPath (Join-Path $root "src\EgyptianDictation.WordAddIn\bin\Release\net48\EgyptianDictation.WordAddIn.dll") -Destination (Join-Path $staging "addin")
Copy-Item -LiteralPath (Join-Path $root "src\EgyptianDictation.Contracts\bin\Release\netstandard2.0\EgyptianDictation.Contracts.dll") -Destination (Join-Path $staging "addin")
Copy-Item -LiteralPath (Join-Path $native "transcribe.dll"),(Join-Path $native "ggml.dll"),(Join-Path $native "ggml-base.dll") -Destination (Join-Path $staging "runtime\cohere")
Copy-Item -LiteralPath (Join-Path $native "ggml-vulkan.dll") -Destination (Join-Path $staging "runtime\cohere")
Get-ChildItem -LiteralPath $native -Filter "ggml-cpu-*.dll" | Copy-Item -Destination (Join-Path $staging "runtime\cohere")
New-Item -ItemType Directory -Force -Path (Join-Path $staging "runtime\cohere\safe-cpu") | Out-Null
Copy-Item -LiteralPath (Join-Path $native "ggml-cpu-x64.dll") -Destination (Join-Path $staging "runtime\cohere\safe-cpu")
Copy-Item -LiteralPath (Join-Path $root "config\engine-settings.json"),(Join-Path $root "config\domain_lexicon.json") -Destination (Join-Path $staging "config")
Copy-Item -LiteralPath (Join-Path $prerequisiteDirectory $prerequisiteManifest.visualCppX64.fileName),(Join-Path $prerequisiteDirectory $prerequisiteManifest.netFramework48.fileName),(Join-Path $prerequisiteDirectory "manifest.json") -Destination (Join-Path $staging "prerequisites")
Copy-Item -LiteralPath (Join-Path $root "docs\THIRD-PARTY-NOTICES.txt"),(Join-Path $root "docs\USER_GUIDE_AR.md"),(Join-Path $root "docs\TERMS_AR.txt"),(Join-Path $root "README.md"),(Join-Path $root "NOTICE.md") -Destination $staging
Copy-Item -LiteralPath (Join-Path $native "licenses\LICENSE") -Destination (Join-Path $staging "licenses\transcribe.cpp-LICENSE")
Copy-Item -LiteralPath (Join-Path $native "licenses\ggml\LICENSE") -Destination (Join-Path $staging "licenses\ggml-LICENSE")
Get-ChildItem -LiteralPath $staging -Filter "*.pdb" -File -Recurse | Remove-Item -Force

$modelHash = if ($model) { (Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash } else { $null }
$manifest = @{
    applicationVersion = $Version
    builtAtUtc = [DateTime]::UtcNow.ToString("o")
    runtime = "Windows x64 CPU/Vulkan auto with CPU fallback, self-contained"
    engine = "transcribe.cpp v0.1.1"
    runtimeCommit = "d89ecb75062e8457681c563994675dc60e31db80"
    model = "CohereLabs/cohere-transcribe-arabic-07-2026"
    quantization = "Q5_K_M"
    modelSha256 = $modelHash
    pythonIncluded = $false
    telemetry = $false
    visualCppX64Version = $prerequisiteManifest.visualCppX64.minimumVersion
    visualCppX64Sha256 = $prerequisiteManifest.visualCppX64.sha256
    netFramework48Sha256 = $prerequisiteManifest.netFramework48.sha256
} | ConvertTo-Json
Set-Content -LiteralPath (Join-Path $staging "release-manifest.json") -Value $manifest -Encoding utf8

Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
function New-Payload([string]$Path, [bool]$WithModel) {
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $staging -File -Recurse) {
            $entry = $file.FullName.Substring($staging.Length + 1).Replace('\','/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
        if ($WithModel) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $model, "models/$modelName", [IO.Compression.CompressionLevel]::NoCompression) | Out-Null
        }
    }
    finally { $zip.Dispose() }
}

function Publish-Installer([string]$Payload, [string]$Name, [string]$OutputDirectory) {
    Copy-Item -LiteralPath $Payload -Destination (Join-Path $installer "payload.zip") -Force
    & $dotnet publish (Join-Path $root "src\EgyptianDictation.Setup\EgyptianDictation.Setup.csproj") `
        -t:Rebuild -c Release -r win-x64 --self-contained true `
        -p:Version=$Version -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "Setup publish failed." }
    $destination = Join-Path $release $Name
    Copy-Item -LiteralPath (Join-Path $OutputDirectory "EgyptianDictationSetup.exe") -Destination $destination -Force
    Get-FileHash -LiteralPath $destination -Algorithm SHA256
}

$appPayload = Join-Path $installer "payload-app.zip"
New-Payload $appPayload $false
Publish-Installer $appPayload "EgyptianDictation-Setup-App-$releaseLabel.exe" (Join-Path $release "setup-app")

if ($IncludeModel) {
    $fullPayload = Join-Path $installer "payload-full.zip"
    New-Payload $fullPayload $true
    Publish-Installer $fullPayload "EgyptianDictation-Setup-Full-$releaseLabel.exe" (Join-Path $release "setup-full")
}
