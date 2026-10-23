# 공유용 zip 만들기: 실행에 필요한 파일만 모아 공유용\RazerChargerChroma.zip 으로
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root "RazerChargerChroma.exe"
if (-not (Test-Path $exe)) { Write-Host "먼저 src\build.bat 으로 빌드하세요."; exit 1 }
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("rcc-" + [Guid]::NewGuid().ToString("N"))
$stage = Join-Path $tmp "RazerChargerChroma"
New-Item -ItemType Directory -Force $stage | Out-Null
$base = (Get-Item $tmp).FullName
try {
    Copy-Item $exe $stage
    Copy-Item (Join-Path $root "lib\*") $stage            # WebView2 DLL 3개 + WebView2-LICENSE.txt
    Copy-Item (Join-Path $root "ui") $stage -Recurse
    Copy-Item (Join-Path $root "LICENSE") $stage
    Copy-Item (Join-Path $PSScriptRoot "사용법.txt") $stage
    Get-ChildItem $root -File | Where-Object { $_.Extension -in ".bat", ".ps1" } | Copy-Item -Destination $stage   # 바로가기 만들기

    $outDir = Join-Path $root "공유용"
    New-Item -ItemType Directory -Force $outDir | Out-Null
    $zipPath = Join-Path $outDir "RazerChargerChroma.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::Open($zipPath, "Create")
    try {
        Get-ChildItem $stage -Recurse -File | ForEach-Object {
            # zip 안 경로는 / 로 (다른 압축 프로그램에서도 폴더로 풀리게), 한글 이름은 UTF-8 표시가 붙음
            $name = $_.FullName.Substring($base.Length + 1).Replace("\", "/")
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $name, "Optimal")
        }
    } finally { $zip.Dispose() }
    Write-Host ("만들었습니다: {0} ({1:N0} 바이트)" -f $zipPath, (Get-Item $zipPath).Length)
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
