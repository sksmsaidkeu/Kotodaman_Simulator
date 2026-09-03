$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Get-RelativeSlashPath {
    # Windows PowerShell 5.1(.NET Framework)에는 [System.IO.Path]::GetRelativePath가 없어 문자열로 계산합니다.
    param([string]$BaseDirectory, [string]$FullPath)
    $baseFull = (Resolve-Path -LiteralPath $BaseDirectory).Path.TrimEnd('\')
    $target = (Resolve-Path -LiteralPath $FullPath).Path
    return $target.Substring($baseFull.Length).TrimStart('\') -replace '\\', '/'
}

$ProjectRoot = Split-Path $PSScriptRoot -Parent
$ProjectData = Join-Path $ProjectRoot 'Data'
$BaselineRoot = Join-Path $ProjectRoot 'ReleaseTools\Baseline'
$PendingUpdatePath = Join-Path $ProjectRoot 'ReleaseTools\pending_data_update.json'
$BundledUpdates = Join-Path $ProjectData 'BundledUpdates'
$SyncScript = Join-Path $PSScriptRoot 'sync_release_data.ps1'

try {
    if (-not (Test-Path -LiteralPath $PendingUpdatePath)) {
        throw 'No pending data update was found. Run create_data_update.bat first.'
    }

    $pending = Get-Content -LiteralPath $PendingUpdatePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $zipPath = [string]$pending.ZipPath
    if ([string]::IsNullOrWhiteSpace($zipPath) -or -not (Test-Path -LiteralPath $zipPath)) {
        throw 'The pending data update ZIP was not found.'
    }

    & $SyncScript -ProjectDataDirectory $ProjectData

    $charactersPath = Join-Path $ProjectData 'characters.json'
    $imagesPath = Join-Path $ProjectData 'CharacterImages'
    $referencesPath = Join-Path $ProjectData 'RecognitionReferences'
    $manifestPath = Join-Path $ProjectData 'data_manifest.json'

    if (-not (Test-Path -LiteralPath $charactersPath)) { throw 'Data\characters.json was not found.' }

    # 이 머신 PS 5.1에서는 이 자리(함수로 감싸지 않은 직접 대입)에서 @()로 배열을 다시 감싸면
    # Count가 실제 요소 수 대신 1로 나오는 버그가 있습니다. ConvertFrom-Json이 JSON 배열에는
    # 이미 System.Object[]를 반환하므로 @() 없이 그대로 씁니다.
    $characterArray = Get-Content -LiteralPath $charactersPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $metadata = [pscustomobject]@{
        SchemaVersion = 1
        DataVersion = [string]$pending.DataVersion
        MinimumAppVersion = '1.25.1'
        UpdatedAt = ([DateTimeOffset]::Now).ToString("o")
        CharacterCount = $characterArray.Count
    }
    $metadataJson = $metadata | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText(
        $manifestPath,
        $metadataJson,
        (New-Object System.Text.UTF8Encoding -ArgumentList $false))

    New-Item -ItemType Directory -Path $BundledUpdates -Force | Out-Null
    $bundledZipPath = Join-Path $BundledUpdates ([System.IO.Path]::GetFileName($zipPath))
    Copy-Item -LiteralPath $zipPath -Destination $bundledZipPath -Force

    New-Item -ItemType Directory -Path $BaselineRoot -Force | Out-Null
    Copy-Item -LiteralPath $charactersPath -Destination (Join-Path $BaselineRoot 'characters.json') -Force
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $BaselineRoot 'data_manifest.json') -Force

    $hashes = New-Object System.Collections.Generic.List[object]
    if (Test-Path -LiteralPath $imagesPath) {
        Get-ChildItem -LiteralPath $imagesPath -File | Sort-Object Name | ForEach-Object {
            $hashes.Add([pscustomobject]@{
                FileName = $_.Name
                Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            })
        }
    }

    $json = $hashes.ToArray() | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText(
        (Join-Path $BaselineRoot 'image_hashes.json'),
        $json,
        (New-Object System.Text.UTF8Encoding -ArgumentList $false))

    # RecognitionReferences는 "<UI 프로필>/<캐릭터>/slot-*.png" 하위 폴더 구조라
    # 파일명이 아니라 Data\RecognitionReferences 기준 상대 경로로 식별합니다.
    $referenceHashes = New-Object System.Collections.Generic.List[object]
    if (Test-Path -LiteralPath $referencesPath) {
        Get-ChildItem -LiteralPath $referencesPath -File -Recurse | Sort-Object FullName | ForEach-Object {
            $relativePath = Get-RelativeSlashPath -BaseDirectory $referencesPath -FullPath $_.FullName
            $referenceHashes.Add([pscustomobject]@{
                RelativePath = $relativePath
                Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            })
        }
    }

    $referenceJson = $referenceHashes.ToArray() | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText(
        (Join-Path $BaselineRoot 'reference_hashes.json'),
        $referenceJson,
        (New-Object System.Text.UTF8Encoding -ArgumentList $false))

    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Remove-Item -LiteralPath $PendingUpdatePath -Force

    Write-Host ''
    Write-Host ("Baseline accepted: {0}" -f $manifest.DataVersion) -ForegroundColor Green
    Write-Host ("Bundled update: {0}" -f $bundledZipPath)
    Write-Host ("Characters: {0}" -f $characterArray.Count)
    Write-Host ("Images: {0}" -f $hashes.Count)
    Write-Host ("Recognition references: {0}" -f $referenceHashes.Count)
    Write-Host 'Future full program releases will apply this data package automatically.' -ForegroundColor Green

    # D-3 (PRD.md): 델타 체인은 Data\BundledUpdates 안의 zip 개수 그 자체다 - 매번 쌓이기만
    # 하고 지워지는 코드 경로가 없으므로, 5개(=5 데이터 버전)부터는 사람이 직접 판단해
    # 오래된 델타를 접어야 한다(어떤 FromDataVersion을 더는 안 지원할지는 실사용자 분포에
    # 달려 있어 자동화하지 않는다).
    $chainLength = @(Get-ChildItem -LiteralPath $BundledUpdates -Filter '*.zip').Count
    Write-Host ("Delta chain length: {0} data version(s) since last snapshot" -f $chainLength) -ForegroundColor Cyan
    if ($chainLength -ge 5) {
        Write-Host ''
        Write-Host '=============================================' -ForegroundColor Yellow
        Write-Host ' D-3 snapshot checkpoint: chain length >= 5' -ForegroundColor Yellow
        Write-Host '=============================================' -ForegroundColor Yellow
        Write-Host 'Consider folding the delta chain before the next release:'
        Write-Host ("  1. List {0} and decide the oldest FromDataVersion still worth" -f $BundledUpdates)
        Write-Host '     supporting (how far behind are real installs, if known).'
        Write-Host '  2. Delete the .zip files older than that floor - users below it'
        Write-Host '     get this DataVersion via a full app reinstall instead.'
        Write-Host '  3. Commit the pruned Data\BundledUpdates alongside this release.'
        Write-Host 'Skip this if every existing delta is still needed.'
    }
}
catch {
    Write-Host ''
    Write-Host 'Baseline update failed.' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
