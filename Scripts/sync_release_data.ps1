param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectDataDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$userDataDirectory = Join-Path $env:LOCALAPPDATA 'KotodamanWordFinder\Data'

if (-not (Test-Path -LiteralPath $userDataDirectory)) {
    Write-Host '[INFO] No local user data was found. The project Data directory will be used as-is.'
    return
}

New-Item -ItemType Directory -Path $ProjectDataDirectory -Force | Out-Null

$sharedFiles = @(
    'characters.json',
    'gaccag_update.json',
    'gaccag_words.json',
    'gaccag_words.json.gz'
)

foreach ($fileName in $sharedFiles) {
    $sourcePath = Join-Path $userDataDirectory $fileName
    if (Test-Path -LiteralPath $sourcePath) {
        $destinationPath = Join-Path $ProjectDataDirectory $fileName
        Copy-Item -LiteralPath $sourcePath -Destination $destinationPath -Force
        Write-Host ("[SYNC] {0}" -f $fileName)
    }
}

$sharedDirectories = @(
    'CharacterImages',
    'RecognitionReferences'
)

foreach ($directoryName in $sharedDirectories) {
    $sourceDirectory = Join-Path $userDataDirectory $directoryName
    if (-not (Test-Path -LiteralPath $sourceDirectory)) {
        continue
    }

    $destinationDirectory = Join-Path $ProjectDataDirectory $directoryName
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null

    Get-ChildItem -LiteralPath $sourceDirectory -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $destinationDirectory -Recurse -Force
    }

    Write-Host ("[SYNC] {0}" -f $directoryName)
}

$compressedDictionaryPath = Join-Path $ProjectDataDirectory 'gaccag_words.json.gz'
$plainDictionaryPath = Join-Path $ProjectDataDirectory 'gaccag_words.json'

if ((Test-Path -LiteralPath $compressedDictionaryPath) -and
    (Test-Path -LiteralPath $plainDictionaryPath)) {
    Remove-Item -LiteralPath $plainDictionaryPath -Force
    Write-Host '[CLEAN] Removed the duplicate uncompressed dictionary.'
}

$charactersPath = Join-Path $ProjectDataDirectory 'characters.json'
$imageDirectory = Join-Path $ProjectDataDirectory 'CharacterImages'

if ((Test-Path -LiteralPath $charactersPath) -and
    (Test-Path -LiteralPath $imageDirectory)) {

    $characters = Get-Content -LiteralPath $charactersPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $referencedImages = New-Object 'System.Collections.Generic.HashSet[string]' `
        ([System.StringComparer]::OrdinalIgnoreCase)

    foreach ($character in @($characters)) {
        if ($null -ne $character.ImageFileName -and
            -not [string]::IsNullOrWhiteSpace([string]$character.ImageFileName)) {
            [void]$referencedImages.Add(
                [System.IO.Path]::GetFileName([string]$character.ImageFileName))
        }

        foreach ($form in @($character.AlternateForms)) {
            if ($null -ne $form -and
                $null -ne $form.ImageFileName -and
                -not [string]::IsNullOrWhiteSpace([string]$form.ImageFileName)) {
                [void]$referencedImages.Add(
                    [System.IO.Path]::GetFileName([string]$form.ImageFileName))
            }
        }
    }

    $removedCount = 0

    Get-ChildItem -LiteralPath $imageDirectory -File | ForEach-Object {
        if (-not $referencedImages.Contains($_.Name)) {
            Remove-Item -LiteralPath $_.FullName -Force
            $removedCount++
        }
    }

    Write-Host ("[CLEAN] Removed {0} unreferenced image file(s)." -f $removedCount)
}

Get-ChildItem -LiteralPath $ProjectDataDirectory -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -like '*.backup' -or
        $_.Name -like '*.tmp' -or
        $_.Name -like '*.seed.tmp'
    } |
    Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host '[OK] Shared user data was synced to the release Data directory.'
