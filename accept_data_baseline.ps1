$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Get-RelativeSlashPath {
    # Windows PowerShell 5.1(.NET Framework)에는 [System.IO.Path]::GetRelativePath가 없어 문자열로 계산합니다.
    param([string]$BaseDirectory, [string]$FullPath)
    $baseFull = (Resolve-Path -LiteralPath $BaseDirectory).Path.TrimEnd('\')
    $target = (Resolve-Path -LiteralPath $FullPath).Path
    return $target.Substring($baseFull.Length).TrimStart('\') -replace '\\', '/'
}

$ProjectRoot = $PSScriptRoot
$ProjectData = Join-Path $ProjectRoot 'Data'
$BaselineRoot = Join-Path $ProjectRoot 'ReleaseTools\Baseline'
$PendingUpdatePath = Join-Path $ProjectRoot 'ReleaseTools\pending_data_update.json'
$BundledUpdates = Join-Path $ProjectData 'BundledUpdates'
$SyncScript = Join-Path $ProjectRoot 'sync_release_data.ps1'

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

    $characterArray = @(Get-Content -LiteralPath $charactersPath -Raw -Encoding UTF8 | ConvertFrom-Json)
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
}
catch {
    Write-Host ''
    Write-Host 'Baseline update failed.' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
