param(
    [Parameter(Mandatory = $true)]
    [string]$DataVersion
)

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
$UpdatesRoot = Join-Path $ProjectRoot 'Updates'
$SyncScript = Join-Path $ProjectRoot 'sync_release_data.ps1'
$BaselineCharacters = Join-Path $BaselineRoot 'characters.json'
$BaselineHashes = Join-Path $BaselineRoot 'image_hashes.json'
$BaselineReferenceHashes = Join-Path $BaselineRoot 'reference_hashes.json'
$BaselineManifest = Join-Path $BaselineRoot 'data_manifest.json'
$CurrentCharacters = Join-Path $ProjectData 'characters.json'
$CurrentImages = Join-Path $ProjectData 'CharacterImages'
$CurrentReferences = Join-Path $ProjectData 'RecognitionReferences'
$PendingUpdatePath = Join-Path $ProjectRoot 'ReleaseTools\pending_data_update.json'
$stagingRoot = $null

function Write-JsonUtf8NoBom {
    param([object]$Value, [string]$Path)
    # ConvertTo-Json에 빈 컬렉션을 파이프로 흘리면(예: 캐릭터 변경이 하나도 없는 릴리스)
    # "[]"가 아니라 $null이 나옵니다. 빈 파일은 C# JsonSerializer가 못 읽으므로 직접 채웁니다.
    if ($Value -is [System.Collections.ICollection] -and $Value.Count -eq 0) {
        $json = '[]'
    }
    else {
        $json = $Value | ConvertTo-Json -Depth 100
    }
    [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding -ArgumentList $false))
}

function Get-CanonicalJson {
    param([object]$Value)
    return ($Value | ConvertTo-Json -Depth 100 -Compress)
}

function Load-JsonArray {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw ("Required JSON file was not found: {0}" -f $Path)
    }
    return @(Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

try {
    $DataVersion = $DataVersion.Trim()
    if ([string]::IsNullOrWhiteSpace($DataVersion)) {
        throw 'DataVersion is empty.'
    }

    if ($DataVersion.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
        throw 'DataVersion contains a character that cannot be used in a file name.'
    }

    if (-not (Test-Path -LiteralPath $SyncScript)) {
        throw ("Sync script was not found: {0}" -f $SyncScript)
    }

    Write-Host '[1/6] Syncing current shared user data...' -ForegroundColor Yellow
    & $SyncScript -ProjectDataDirectory $ProjectData

    if (-not (Test-Path -LiteralPath $BaselineCharacters) -or
        -not (Test-Path -LiteralPath $BaselineHashes) -or
        -not (Test-Path -LiteralPath $BaselineManifest)) {
        throw 'The release baseline is missing. Run accept_data_baseline.bat once before creating updates.'
    }

    $baselineManifestObject = Get-Content -LiteralPath $BaselineManifest -Raw -Encoding UTF8 | ConvertFrom-Json
    $fromDataVersion = [string]$baselineManifestObject.DataVersion
    if ([string]::IsNullOrWhiteSpace($fromDataVersion)) {
        throw 'The baseline DataVersion is empty.'
    }

    try {
        $fromVersionObject = [Version]$fromDataVersion
        $newVersionObject = [Version]$DataVersion
    }
    catch {
        throw 'DataVersion must use a numeric format such as 2026.08.20.1.'
    }

    if ($newVersionObject -le $fromVersionObject) {
        throw ("The new DataVersion must be greater than {0}." -f $fromDataVersion)
    }

    Write-Host '[2/6] Comparing character records...' -ForegroundColor Yellow
    $baselineCharactersArray = Load-JsonArray $BaselineCharacters
    $currentCharactersArray = Load-JsonArray $CurrentCharacters

    $baselineMap = @{}
    foreach ($character in $baselineCharactersArray) {
        $id = [string]$character.Id
        if (-not [string]::IsNullOrWhiteSpace($id)) { $baselineMap[$id] = $character }
    }

    $currentMap = @{}
    foreach ($character in $currentCharactersArray) {
        $id = [string]$character.Id
        if (-not [string]::IsNullOrWhiteSpace($id)) { $currentMap[$id] = $character }
    }

    $changes = New-Object System.Collections.Generic.List[object]
    $added = 0
    $updated = 0
    $deleted = 0
    $allIds = @(@($baselineMap.Keys) + @($currentMap.Keys) | Sort-Object -Unique)
    foreach ($id in $allIds) {
        $hasBaseline = $baselineMap.ContainsKey($id)
        $hasCurrent = $currentMap.ContainsKey($id)

        if (-not $hasBaseline -and $hasCurrent) {
            $changes.Add([pscustomobject]@{
                ChangeType = 'Add'
                Id = $id
                Previous = $null
                Current = $currentMap[$id]
            })
            $added++
            continue
        }

        if ($hasBaseline -and -not $hasCurrent) {
            $changes.Add([pscustomobject]@{
                ChangeType = 'Delete'
                Id = $id
                Previous = $baselineMap[$id]
                Current = $null
            })
            $deleted++
            continue
        }

        $beforeJson = Get-CanonicalJson -Value $baselineMap[$id]
        $afterJson = Get-CanonicalJson -Value $currentMap[$id]
        if ($beforeJson -ne $afterJson) {
            $changes.Add([pscustomobject]@{
                ChangeType = 'Update'
                Id = $id
                Previous = $baselineMap[$id]
                Current = $currentMap[$id]
            })
            $updated++
        }
    }

    Write-Host '[3/6] Comparing character images...' -ForegroundColor Yellow
    $baselineHashArray = Load-JsonArray $BaselineHashes
    $baselineHashMap = @{}
    foreach ($item in $baselineHashArray) {
        $baselineHashMap[[string]$item.FileName] = [string]$item.Sha256
    }

    $currentHashMap = @{}
    if (Test-Path -LiteralPath $CurrentImages) {
        Get-ChildItem -LiteralPath $CurrentImages -File | ForEach-Object {
            $currentHashMap[$_.Name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    }

    $imageChanges = New-Object System.Collections.Generic.List[object]
    $allImageNames = @(@($baselineHashMap.Keys) + @($currentHashMap.Keys) | Sort-Object -Unique)

    $safeVersion = $DataVersion -replace '[^0-9A-Za-z._-]', '_'
    $stagingRoot = Join-Path $env:TEMP ("KotodamanDataUpdate_{0}_{1}" -f $safeVersion, [Guid]::NewGuid().ToString('N'))
    $stagingImages = Join-Path $stagingRoot 'CharacterImages'
    $stagingReferences = Join-Path $stagingRoot 'RecognitionReferences'
    New-Item -ItemType Directory -Path $stagingImages -Force | Out-Null
    New-Item -ItemType Directory -Path $stagingReferences -Force | Out-Null

    foreach ($fileName in $allImageNames) {
        $hasBaseline = $baselineHashMap.ContainsKey($fileName)
        $hasCurrent = $currentHashMap.ContainsKey($fileName)
        $changeType = $null

        if (-not $hasBaseline -and $hasCurrent) { $changeType = 'Add' }
        elseif ($hasBaseline -and -not $hasCurrent) { $changeType = 'Delete' }
        elseif ($baselineHashMap[$fileName] -ne $currentHashMap[$fileName]) { $changeType = 'Update' }

        if ($null -eq $changeType) { continue }

        $archivePath = ''
        if ($changeType -ne 'Delete') {
            $archivePath = 'CharacterImages/' + $fileName
            Copy-Item -LiteralPath (Join-Path $CurrentImages $fileName) -Destination (Join-Path $stagingImages $fileName) -Force
        }

        $imageChanges.Add([pscustomobject]@{
            ChangeType = $changeType
            FileName = $fileName
            ArchivePath = $archivePath
            PreviousSha256 = $(if ($hasBaseline) { $baselineHashMap[$fileName] } else { $null })
            CurrentSha256 = $(if ($hasCurrent) { $currentHashMap[$fileName] } else { $null })
        })
    }

    Write-Host '[4/6] Comparing recognition reference images...' -ForegroundColor Yellow
    $baselineReferenceHashMap = @{}
    if (Test-Path -LiteralPath $BaselineReferenceHashes) {
        foreach ($item in (Load-JsonArray $BaselineReferenceHashes)) {
            $baselineReferenceHashMap[[string]$item.RelativePath] = [string]$item.Sha256
        }
    }

    $currentReferenceHashMap = @{}
    if (Test-Path -LiteralPath $CurrentReferences) {
        Get-ChildItem -LiteralPath $CurrentReferences -File -Recurse | ForEach-Object {
            $relativePath = Get-RelativeSlashPath -BaseDirectory $CurrentReferences -FullPath $_.FullName
            $currentReferenceHashMap[$relativePath] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    }

    $referenceChanges = New-Object System.Collections.Generic.List[object]
    $allReferencePaths = @(@($baselineReferenceHashMap.Keys) + @($currentReferenceHashMap.Keys) | Sort-Object -Unique)

    foreach ($relativePath in $allReferencePaths) {
        $hasBaseline = $baselineReferenceHashMap.ContainsKey($relativePath)
        $hasCurrent = $currentReferenceHashMap.ContainsKey($relativePath)
        $changeType = $null

        if (-not $hasBaseline -and $hasCurrent) { $changeType = 'Add' }
        elseif ($hasBaseline -and -not $hasCurrent) { $changeType = 'Delete' }
        elseif ($baselineReferenceHashMap[$relativePath] -ne $currentReferenceHashMap[$relativePath]) { $changeType = 'Update' }

        if ($null -eq $changeType) { continue }

        $archivePath = ''
        if ($changeType -ne 'Delete') {
            $archivePath = 'RecognitionReferences/' + $relativePath
            $sourceFile = Join-Path $CurrentReferences $relativePath
            $destinationFile = Join-Path $stagingReferences $relativePath
            New-Item -ItemType Directory -Path (Split-Path -Parent $destinationFile) -Force | Out-Null
            Copy-Item -LiteralPath $sourceFile -Destination $destinationFile -Force
        }

        $referenceChanges.Add([pscustomobject]@{
            ChangeType = $changeType
            RelativePath = $relativePath
            ArchivePath = $archivePath
            PreviousSha256 = $(if ($hasBaseline) { $baselineReferenceHashMap[$relativePath] } else { $null })
            CurrentSha256 = $(if ($hasCurrent) { $currentReferenceHashMap[$relativePath] } else { $null })
        })
    }

    $manifest = [pscustomobject]@{
        SchemaVersion = 1
        PackageType = 'KotodamanDataUpdate'
        FromDataVersion = $fromDataVersion
        DataVersion = $DataVersion
        MinimumAppVersion = '1.25.1'
        CreatedAt = ([DateTimeOffset]::Now).ToString("o")
        AddedCharacterCount = $added
        UpdatedCharacterCount = $updated
        DeletedCharacterCount = $deleted
        Images = $imageChanges.ToArray()
        References = $referenceChanges.ToArray()
    }

    Write-Host '[5/6] Writing update package files...' -ForegroundColor Yellow
    Write-JsonUtf8NoBom -Value $manifest -Path (Join-Path $stagingRoot 'manifest.json')
    Write-JsonUtf8NoBom -Value $changes.ToArray() -Path (Join-Path $stagingRoot 'characters_delta.json')

    New-Item -ItemType Directory -Path $UpdatesRoot -Force | Out-Null
    $safeFromVersion = $fromDataVersion -replace '[^0-9A-Za-z._-]', '_'
    $zipPath = Join-Path $UpdatesRoot ("KotodamanData_{0}_from_{1}.zip" -f $safeVersion, $safeFromVersion)
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

    Write-Host '[6/6] Creating ZIP...' -ForegroundColor Yellow
    Compress-Archive -Path (Join-Path $stagingRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal -Force

    $zipSize = [Math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 2)
    $pending = [pscustomobject]@{
        FromDataVersion = $fromDataVersion
        DataVersion = $DataVersion
        ZipPath = $zipPath
        CreatedAt = ([DateTimeOffset]::Now).ToString("o")
    }
    Write-JsonUtf8NoBom -Value $pending -Path $PendingUpdatePath

    Write-Host ''
    Write-Host 'Data update package created.' -ForegroundColor Green
    Write-Host ("Version: {0} -> {1}" -f $fromDataVersion, $DataVersion)
    Write-Host ("Characters: +{0} / ~{1} / -{2}" -f $added, $updated, $deleted)
    Write-Host ("Images: {0}" -f $imageChanges.Count)
    Write-Host ("Recognition references: {0}" -f $referenceChanges.Count)
    Write-Host ("ZIP: {0}" -f $zipPath)
    Write-Host ("Size: {0} MB" -f $zipSize)
    Write-Host ''
    Write-Host 'Do not run accept_data_baseline.bat until this ZIP has been tested and distributed.' -ForegroundColor Yellow
    try { Start-Process explorer.exe -ArgumentList $UpdatesRoot } catch { }
}
catch {
    Write-Host ''
    Write-Host 'Data update package creation failed.' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
finally {
    if ($null -ne $stagingRoot -and (Test-Path -LiteralPath $stagingRoot)) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
