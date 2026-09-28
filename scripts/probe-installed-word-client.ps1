param(
    [string]$AssemblyPath = "C:\Program Files\EgyptianDictation\addin\EgyptianDictation.WordAddIn.dll"
)

$ErrorActionPreference = "Stop"
$assemblyPath = (Resolve-Path -LiteralPath $AssemblyPath).Path
$assembly = [Reflection.Assembly]::LoadFrom($assemblyPath)
$clientType = $assembly.GetType("EgyptianDictation.WordAddIn.WordPipeClient", $true)
$client = [Activator]::CreateInstance($clientType, $true)
try {
    $start = $clientType.GetMethod("Send").Invoke($client, @("start_session", "installed-probe", $null))
    Write-Output "START_TYPE=$($start.GetType().GetProperty('Type').GetValue($start))"
    Write-Output "START_PAYLOAD=$($start.GetType().GetProperty('PayloadJson').GetValue($start))"
    $stop = $clientType.GetMethod("Send").Invoke($client, @("stop_session", "installed-probe", $null))
    Write-Output "STOP_TYPE=$($stop.GetType().GetProperty('Type').GetValue($stop))"
    Write-Output "STOP_PAYLOAD=$($stop.GetType().GetProperty('PayloadJson').GetValue($stop))"
}
finally {
    $clientType.GetMethod("Dispose").Invoke($client, @())
}
