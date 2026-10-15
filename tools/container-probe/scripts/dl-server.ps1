$ErrorActionPreference = 'Continue'
$dir = 'C:\wsm-images\server-ltsc2022.src'
New-Item -ItemType Directory -Force $dir | Out-Null
$repo = 'windows/server'
$base = "https://mcr.microsoft.com/v2/$repo"
$h = @{ Accept = 'application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.v2+json' }

$ml = Invoke-RestMethod -Headers $h -Uri "$base/manifests/ltsc2022"
$amd = $ml.manifests | Where-Object { $_.platform.os -eq 'windows' -and $_.platform.architecture -eq 'amd64' } | Select-Object -First 1
$man = Invoke-RestMethod -Headers $h -Uri "$base/manifests/$($amd.digest)"

$i = 0
foreach ($l in $man.layers) {
  $i++
  $file = Join-Path $dir "layer$i.tgz"
  if (Test-Path $file) { Write-Host "exists $file"; continue }
  Write-Host "downloading layer$i ($([math]::Round($l.size/1MB,1)) MB)"
  & curl.exe -L --fail --retry 3 --retry-delay 2 -o $file "$base/blobs/$($l.digest)"
  Write-Host "exit=$LASTEXITCODE size=$((Get-Item $file -ErrorAction SilentlyContinue).Length)"
}
Write-Host "done"
