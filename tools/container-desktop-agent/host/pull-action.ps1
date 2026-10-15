if (Test-Path 'C:\wsm\shot.png') { [Convert]::ToBase64String([IO.File]::ReadAllBytes('C:\wsm\shot.png')) } else { 'MISSING' }
