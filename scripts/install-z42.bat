@echo off
setlocal enabledelayedexpansion
REM install-z42.bat — z42-bootstrap (Windows). Downloads the prebuilt z42
REM launcher package (windows-x64 .zip) into <repo>\.z42 (project-local,
REM gitignored). Mirrors scripts/install-z42.sh. The real work is in PowerShell.
REM Version from versions.toml [toolchain.z42].launcher (default nightly).

set "REPO=%~dp0.."
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$repo=(Resolve-Path '%REPO%').Path;" ^
  "$dest=Join-Path $repo '.z42'; $slug='codesigner-ui/z42';" ^
  "$stamp=Join-Path $dest '.bootstrap-stamp';" ^
  "$ver='nightly'; $intoml=$false;" ^
  "foreach($l in Get-Content (Join-Path $repo 'versions.toml')){" ^
  "  if($l -match '^\[toolchain\.z42\]'){$intoml=$true;continue}" ^
  "  if($l -match '^\['){$intoml=$false}" ^
  "  if($intoml -and $l -match '^launcher.*\"([^\"]+)\"'){$ver=$Matches[1];break}}" ^
  "$tag = if($ver -eq 'nightly'){'nightly'}else{'v'+$ver};" ^
  "$rid='windows-x64'; $asset=\"z42-$ver-$rid.zip\";" ^
  "$url=\"https://github.com/$slug/releases/download/$tag/$asset\";" ^
  "$id = if($ver -eq 'nightly'){(Invoke-RestMethod \"https://api.github.com/repos/$slug/releases/tags/nightly\").published_at}else{$tag};" ^
  "$want=\"$ver`:$rid`:$id\";" ^
  "if((Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $want)){Write-Host \"install-z42: .z42 up to date ($ver / $rid)\"; exit 0}" ^
  "Write-Host \"install-z42: fetching $asset ($tag) -> $dest\";" ^
  "$tmp=Join-Path $env:TEMP ('z42boot-'+[guid]::NewGuid());" ^
  "New-Item -ItemType Directory -Path $tmp | Out-Null;" ^
  "$zip=Join-Path $tmp $asset;" ^
  "Invoke-WebRequest -Uri $url -OutFile $zip;" ^
  "try{$sums=(Invoke-WebRequest -Uri \"https://github.com/$slug/releases/download/$tag/SHA256SUMS\").Content;" ^
  "  $want_hash=($sums -split \"`n\" | Where-Object {$_ -match [regex]::Escape($asset)+'$'} | Select-Object -First 1).Split(' ')[0];" ^
  "  if($want_hash){$got=(Get-FileHash $zip -Algorithm SHA256).Hash.ToLower();" ^
  "    if($want_hash.ToLower() -ne $got){Write-Error 'install-z42: SHA256 mismatch'; exit 1}; Write-Host 'install-z42: SHA256 ok'}}catch{}" ^
  "Expand-Archive -Path $zip -DestinationPath $tmp -Force;" ^
  "$inner=Join-Path $tmp \"z42-$ver-$rid-release\";" ^
  "if(-not (Test-Path $inner)){$inner=(Get-ChildItem $tmp -Directory -Filter 'z42-*' | Select-Object -First 1).FullName}" ^
  "if(Test-Path $dest){Remove-Item $dest -Recurse -Force}; New-Item -ItemType Directory -Path $dest | Out-Null;" ^
  "Copy-Item (Join-Path $inner '*') $dest -Recurse -Force;" ^
  "Set-Content -Path $stamp -Value $want -NoNewline;" ^
  "Remove-Item $tmp -Recurse -Force;" ^
  "$entry = if(Test-Path (Join-Path $dest 'z42.exe')){Join-Path $dest 'z42.exe'}else{Join-Path $dest 'bin\z42.exe'};" ^
  "Write-Host \"install-z42: installed $ver / $rid -> $dest\"; Write-Host \"  entry: $entry\""
endlocal
