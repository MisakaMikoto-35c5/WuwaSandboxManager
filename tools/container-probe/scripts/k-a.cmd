cmd.exe /c qwinsta & echo ---- & reg query "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" /v AllowListenersInsideArgonContainer & echo ---- & sc query TermService & sc query UmRdpService
