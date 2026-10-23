$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "RazerChargerChroma.exe"
if (-not (Test-Path $exe)) { Write-Host "RazerChargerChroma.exe 를 찾지 못했습니다: $exe"; exit 1 }
$desktop = [Environment]::GetFolderPath("Desktop")
$lnk = Join-Path $desktop "Razer Chroma 충전기.lnk"
$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk)
$s.TargetPath = $exe
$s.WorkingDirectory = $PSScriptRoot
$s.IconLocation = "$exe,0"
$s.Description = "Razer 무선 충전기 크로마 조명"
$s.Save()
Write-Host "바탕화면에 바로가기를 만들었습니다: $lnk"
