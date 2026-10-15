$ErrorActionPreference='Continue'
Write-Output '--- container pipes (all, sorted) ---'
(Get-ChildItem '\\.\pipe\' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name | Sort-Object) -join "`n" | Write-Output
