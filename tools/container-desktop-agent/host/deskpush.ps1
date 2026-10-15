# deskpush.ps1 (HOST side) - send an input command to the container agent.
#
# The host cannot write the raw volume (no elevation), but it *can* write plain
# files into the image layer, which the container sees through its merged C:\.
# Each command goes into its own fresh file, because the container's filesystem
# keeps a handle on a layer file once it has been read - rewriting one inbox file
# would fail with "used by another process".
#
#   powershell -File deskpush.ps1 -Cmd 'keys F3'
#   powershell -File deskpush.ps1 -Cmd "click 250,180`ntext hello"
param(
    [Parameter(Mandatory = $true)][string] $Cmd,
    [string] $InboxDir = 'C:\wsm-images\servercore-ltsc2022\Files\wsm'
)
if (-not (Test-Path $InboxDir)) { throw "no such layer directory: $InboxDir" }
$name = 'inbox-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff') + '.txt'
$target = Join-Path $InboxDir $name
[IO.File]::WriteAllText($target, $Cmd, [Text.Encoding]::ASCII)
Write-Output ("pushed {0}: {1}" -f $name, (($Cmd -replace "`r?`n", ' | ')))
