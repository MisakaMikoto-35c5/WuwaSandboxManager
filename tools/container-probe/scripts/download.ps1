$ErrorActionPreference = 'Continue'
$dir = 'C:\wsmprobe\img'
New-Item -ItemType Directory -Force $dir | Out-Null
$repo = 'https://mcr.microsoft.com/v2/windows/servercore'
$layers = @(
  @{ digest = 'sha256:425c2915a6513db3e98e1410b3ec215cd64abeca589e32e4f5a2a932c2a7242f'; file = "$dir\layer1.tgz" },
  @{ digest = 'sha256:671707c412bd87ace1d6919e04872a50ee14758c0005ff5ff74858187814473d'; file = "$dir\layer2.tgz" }
)
foreach ($l in $layers) {
  if (Test-Path $l.file) { Write-Host "exists $($l.file)"; continue }
  Write-Host "downloading $($l.digest) -> $($l.file)"
  & curl.exe -L --fail --retry 3 --retry-delay 2 -o $l.file "$repo/blobs/$($l.digest)"
  Write-Host "exit=$LASTEXITCODE size=$((Get-Item $l.file -ErrorAction SilentlyContinue).Length)"
}
Write-Host "done"