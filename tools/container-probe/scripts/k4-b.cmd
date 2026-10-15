cmd.exe /c wevtutil qe System "/q:*[System[(EventID=7000 or EventID=7001 or EventID=7024 or EventID=7031 or EventID=7034)]]" /c:12 /rd:true /f:text
