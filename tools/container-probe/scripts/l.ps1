$out="$env:TEMP\opencode\wsmdiag\layers.txt"
"==== CmService Layers (all values) ====" | Set-Content $out
reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Containers\CmService\Layers" /s 2>&1 | Add-Content $out
"==== containers storage baseimages ====" | Add-Content $out
reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Containers" /s 2>&1 | Select-String 'BaseImage|Latest|Version|Path' | Add-Content $out
"==== hcsdiag list -raw ====" | Add-Content $out
hcsdiag list -raw 2>&1 | Add-Content $out
"==== hcs list via Get-ComputeProcess ====" | Add-Content $out
Get-ComputeProcess 2>&1 | Format-List * | Out-String | Add-Content $out
"==== done ====" | Add-Content $out
