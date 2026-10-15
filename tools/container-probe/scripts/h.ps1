$out="$env:TEMP\opencode\wsmdiag\hcsdiag.txt"
hcsdiag 2>&1 | Out-String | Set-Content $out
"==== list ====" | Add-Content $out
hcsdiag list 2>&1 | Out-String | Add-Content $out
"==== help exec ====" | Add-Content $out
hcsdiag help 2>&1 | Out-String | Add-Content $out
