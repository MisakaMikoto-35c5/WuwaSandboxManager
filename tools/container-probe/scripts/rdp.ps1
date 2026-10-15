$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$base1 = 'C:\wsmprobe\img\base1'
$natId = 'CC1FA2D0-0339-4DFA-A407-6425C880AE85'
$id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee00c0"
$scratch = "C:\wsmprobe\ws22rdp"
$log = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\rdp.log"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
$env:WSMPROBE_HOLD = "600"
& $exe rdp $id $scratch $base1 $natId *> $log
