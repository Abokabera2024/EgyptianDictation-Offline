$ErrorActionPreference = "Stop"
$word = [Runtime.InteropServices.Marshal]::GetActiveObject("Word.Application")
$addIn = $word.COMAddIns.Item("EgyptianDictation.WordAddIn")
Write-Output "CONNECT=$($addIn.Connect)"
Write-Output "OBJECT_NULL=$($null -eq $addIn.Object)"
