$ErrorActionPreference = 'Stop'
$h = @{ Accept = 'application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.v2+json' }

foreach ($spec in @(
  @{ repo = 'windows/server'; tag = 'ltsc2022' },
  @{ repo = 'windows/server'; tag = 'ltsc2025' },
  @{ repo = 'windows/servercore'; tag = 'ltsc2022' },
  @{ repo = 'windows'; tag = 'ltsc2019' }
)) {
  $repo = $spec.repo; $tag = $spec.tag
  "===== ${repo}:$tag ====="
  try {
    $ml = Invoke-RestMethod -Headers $h -Uri "https://mcr.microsoft.com/v2/$repo/manifests/$tag"
    if ($ml.manifests) {
      $amd = $ml.manifests | Where-Object { $_.platform.os -eq 'windows' -and $_.platform.architecture -eq 'amd64' } | Select-Object -First 1
      if (-not $amd) { "  no windows/amd64"; continue }
      $man = Invoke-RestMethod -Headers $h -Uri "https://mcr.microsoft.com/v2/$repo/manifests/$($amd.digest)"
    } else {
      $man = $ml
    }
    $total = 0
    foreach ($l in $man.layers) {
      $total += $l.size
      "  layer $([math]::Round($l.size/1MB,1)) MB"
    }
    "  TOTAL compressed: $([math]::Round($total/1MB,1)) MB"
    if ($man.config) {
      # fetch config for the os version
      $cfg = Invoke-RestMethod -Headers $h -Uri "https://mcr.microsoft.com/v2/$repo/blobs/$($man.config.digest)"
      "  os.version: $($cfg.os.version) created: $($cfg.created)"
    }
  } catch { "  ERR: $($_.Exception.Message)" }
}
