$ErrorActionPreference = 'Continue'

Write-Output '==== identity ===='
$wi = [Security.Principal.WindowsIdentity]::GetCurrent()
Write-Output ("  pid={0} user={1} sid={2}" -f $PID, $wi.Name, $wi.User.Value)

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Native {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr sa, uint prot, uint maxHi, uint maxLo, string name);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr MapViewOfFile(IntPtr h, uint access, uint offHi, uint offLo, ulong length);
}
'@
$PAGE_READWRITE = [uint32]0x04
$FILE_MAP_ALL = [uint32]0xF001F

# NOTE: a PowerShell function returns *everything* written to its output stream, so
# informational output must not share it with the values we want back. Keep the
# handles in script scope.
$script:SecH = [IntPtr]::Zero
$script:SecV = [IntPtr]::Zero
function New-Section([string] $name, [int] $capacity, [string] $marker) {
    $script:SecH = [IntPtr]::Zero
    $script:SecV = [IntPtr]::Zero
    $h = [Native]::CreateFileMappingW([IntPtr](-1), [IntPtr]::Zero, $PAGE_READWRITE, 0, [uint32]$capacity, $name)
    $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    if ($h -eq [IntPtr]::Zero) {
        Write-Output ("  !! CreateFileMappingW('$name') failed err=$err")
        return
    }
    $script:SecH = $h
    $view = [Native]::MapViewOfFile($h, $FILE_MAP_ALL, 0, 0, [uint64]$capacity)
    $script:SecV = $view
    if ($view -ne [IntPtr]::Zero) {
        $bytes = [Text.Encoding]::ASCII.GetBytes($marker)
        [Runtime.InteropServices.Marshal]::Copy($bytes, 0, $view, $bytes.Length)
        [Runtime.InteropServices.Marshal]::Copy($bytes, 0, [IntPtr]::Add($view, $capacity - 64), $bytes.Length)
    }
    Write-Output ("  {0,-24} handle=0x{1:X} err={2} view=0x{3:X} cap={4}" -f $name, $h.ToInt64(), $err, $view.ToInt64(), $capacity)
}

Write-Output '==== create sections and publish their handles ===='
$frameCapacity = 64 + 1024 * 768 * 4      # exactly the real frame file size
New-Section 'Global\wsm-silo-test' 65536 'WSM-NAMED-OK'
$namedHandle = $script:SecH
New-Section $null 131072 'WSM-UNNAMED-OK'
$unnamedHandle = $script:SecH
New-Section $null $frameCapacity 'WSM-FRAME-OK'
$frameHandle = $script:SecH

$out = @(
    "pid=$PID",
    "handle_named=$($namedHandle.ToInt64())",
    "handle_unnamed=$($unnamedHandle.ToInt64())",
    "handle_frame=$($frameHandle.ToInt64())",
    "frame_capacity=$frameCapacity"
) -join "`n"
Set-Content -Path 'C:\wsm\shm-handles.txt' -Value $out
Write-Output ('  published: ' + ((Get-Content 'C:\wsm\shm-handles.txt' -Raw) -replace "`n", ' | '))

Write-Output '==== holding the sections alive for 240s ===='
Start-Sleep -Seconds 240
Write-Output '==== done ===='
