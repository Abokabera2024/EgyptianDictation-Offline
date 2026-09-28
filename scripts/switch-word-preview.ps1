param([switch]$Restore)

$ErrorActionPreference = 'Stop'
if (Get-Process WINWORD -ErrorAction SilentlyContinue) {
    throw 'Close Word normally before changing the add-in registration. Your documents are not touched.'
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$assembly = Join-Path $projectRoot 'artifacts\word-preview\addin\EgyptianDictation.WordAddIn.Preview.dll'
$guid = '{63B19D6A-2D3F-4D7C-AB71-F2C85D82A497}'
$progId = 'EgyptianDictation.WordAddIn.Preview'
$classPath = "Software\Classes\CLSID\$guid"
$progPath = "Software\Classes\$progId"
$codeBase = ([Uri]$assembly).AbsoluteUri

if (-not $Restore -and -not (Test-Path -LiteralPath $assembly)) {
    throw "Build the preview first: $assembly"
}

foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
    $root = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
    try {
        if ($Restore) {
            $marker = $root.OpenSubKey("$classPath\InprocServer32")
            $registered = if ($marker) { $marker.GetValue('EgyptianDictationPreview') } else { $null }
            if ($marker) { $marker.Dispose() }
            if ($registered -eq $codeBase) {
                $root.DeleteSubKeyTree($classPath, $false)
                $root.DeleteSubKeyTree($progPath, $false)
                $root.DeleteSubKeyTree("Software\Microsoft\Office\Word\Addins\$progId", $false)
            }
            continue
        }
        $previous = $root.OpenSubKey($classPath)
        if ($previous) {
            $marker = $previous.OpenSubKey('InprocServer32')
            $registered = if ($marker) { $marker.GetValue('EgyptianDictationPreview') } else { $null }
            if ($marker) { $marker.Dispose() }
            $previous.Dispose()
            if ($registered -ne $codeBase) {
                throw "A per-user COM registration already exists in $view; refusing to overwrite it."
            }
        }
        $class = $root.CreateSubKey($classPath)
        $class.SetValue('', 'EgyptianDictation.WordAddIn.WordAddIn')
        $class.CreateSubKey('Implemented Categories\{62C8FE65-4EBB-45e7-B440-6E39B2CDBF29}').Dispose()
        $class.CreateSubKey('ProgId').SetValue('', $progId)
        $class.Dispose()
        foreach ($keyPath in @("$classPath\InprocServer32", "$classPath\InprocServer32\1.0.0.0")) {
            $key = $root.CreateSubKey($keyPath)
            $key.SetValue('', 'mscoree.dll')
            $key.SetValue('ThreadingModel', 'Both')
            $key.SetValue('Class', 'EgyptianDictation.WordAddIn.WordAddIn')
            $key.SetValue('Assembly', 'EgyptianDictation.WordAddIn.Preview, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null')
            $key.SetValue('RuntimeVersion', 'v4.0.30319')
            $key.SetValue('CodeBase', $codeBase)
            if ($keyPath -eq "$classPath\InprocServer32") {
                $key.SetValue('EgyptianDictationPreview', $codeBase)
            }
            $key.Dispose()
        }
        $prog = $root.CreateSubKey($progPath)
        $prog.SetValue('', 'EgyptianDictation.WordAddIn.WordAddIn')
        $prog.CreateSubKey('CLSID').SetValue('', $guid)
        $prog.Dispose()
        $office = $root.CreateSubKey("Software\Microsoft\Office\Word\Addins\$progId")
        $office.SetValue('FriendlyName', 'Egyptian Dictation Preview')
        $office.SetValue('Description', 'Separate offline Word preview build')
        $office.SetValue('LoadBehavior', 3, [Microsoft.Win32.RegistryValueKind]::DWord)
        $office.SetValue('CommandLineSafe', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        $office.Dispose()
    }
    finally { $root.Dispose() }
}
Write-Output $(if ($Restore) { 'Installed Word add-in registration restored.' } else { 'Word preview activated for this user. Open Word and test.' })
