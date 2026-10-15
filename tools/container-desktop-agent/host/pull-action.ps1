if (Test-Path 'C:\wsm\find.png') { [Convert]::ToBase64String([IO.File]::ReadAllBytes('C:\wsm\find.png')) } else { 'MISSING' }
