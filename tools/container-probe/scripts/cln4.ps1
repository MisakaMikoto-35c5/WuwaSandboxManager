$ErrorActionPreference = 'Continue'
$layer = 'C:\wsm-images\server-ltsc2022'
& cmd.exe /c "attrib -r -s -h /s /d `"$layer`" >nul 2>&1"
& cmd.exe /c "rmdir /s /q `"$layer`"" 2>&1 | Out-Null
Start-Sleep -Seconds 2
if (Test-Path $layer) { & cmd.exe /c "rmdir /s /q `"$layer`"" 2>&1 | Out-Null }
"exists: $(Test-Path $layer)"
Get-ChildItem $layer -Recurse -Force -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum | ForEach-Object { "remaining bytes: $($_.Sum)" }
Get-PSDrive C | Select-Object @{n='FreeGB';e={[math]::Round($_.Free/1GB,1)}} | Format-List
