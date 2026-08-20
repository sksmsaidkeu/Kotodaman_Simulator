$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$ProjectRoot = $PSScriptRoot
$ProjectPath = Join-Path $ProjectRoot 'KotodamanWordFinder.csproj'
$PublishRoot = Join-Path $ProjectRoot 'Publish'
$ProjectDataDirectory = Join-Path $ProjectRoot 'Data'

function Wait-ForClose {
    try { [void](Read-Host 'Press Enter to close') }
    catch { Start-Sleep -Seconds 3 }
}

try {
    Set-Location -LiteralPath $ProjectRoot

    if (-not (Test-Path -LiteralPath $ProjectPath)) {
        throw ("Project file not found: {0}" -f $ProjectPath)
    }

    [xml]$projectXml = Get-Content -LiteralPath $ProjectPath -Raw -Encoding UTF8
    $versionNode = $projectXml.SelectSingleNode('//Project/PropertyGroup/Version')
    if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
        throw 'The Version element was not found in KotodamanWordFinder.csproj.'
    }

    $AppVersion = $versionNode.InnerText.Trim()
    $OutputDirectory = Join-Path $PublishRoot ("KotodamanWordFinder_v{0}_win-x64" -f $AppVersion)
    $ZipPath = Join-Path $PublishRoot ("KotodamanWordFinder_v{0}_win-x64_portable.zip" -f $AppVersion)

    Write-Host ''
    Write-Host '=============================================' -ForegroundColor Cyan
    Write-Host (" Kotodaman Word Finder v{0} Publish Tool" -f $AppVersion) -ForegroundColor Cyan
    Write-Host '=============================================' -ForegroundColor Cyan
    Write-Host ''

    $DotnetCommand = Get-Command 'dotnet.exe' -ErrorAction SilentlyContinue
    if ($null -eq $DotnetCommand) { $DotnetCommand = Get-Command 'dotnet' -ErrorAction SilentlyContinue }
    if ($null -eq $DotnetCommand) {
        throw '.NET 8 SDK was not found. Install the .NET 8 SDK x64 and try again.'
    }

    if (Test-Path -LiteralPath $OutputDirectory) { Remove-Item -LiteralPath $OutputDirectory -Recurse -Force }
    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    New-Item -ItemType Directory -Path $PublishRoot -Force | Out-Null

    Write-Host '[1/5] Validating accepted release data...' -ForegroundColor Yellow
    $releaseCharacters = Join-Path $ProjectDataDirectory 'characters.json'
    $releaseManifest = Join-Path $ProjectDataDirectory 'data_manifest.json'
    if (-not (Test-Path -LiteralPath $releaseCharacters)) {
        throw 'Accepted Data\characters.json was not found.'
    }
    if (-not (Test-Path -LiteralPath $releaseManifest)) {
        throw 'Accepted Data\data_manifest.json was not found.'
    }

    $baselineRoot = Join-Path $ProjectRoot 'ReleaseTools\Baseline'
    $baselineCharacters = Join-Path $baselineRoot 'characters.json'
    $baselineManifest = Join-Path $baselineRoot 'data_manifest.json'
    $baselineHashes = Join-Path $baselineRoot 'image_hashes.json'
    $baselineReferenceHashes = Join-Path $baselineRoot 'reference_hashes.json'
    if (-not (Test-Path -LiteralPath $baselineCharacters) -or
        -not (Test-Path -LiteralPath $baselineManifest) -or
        -not (Test-Path -LiteralPath $baselineHashes) -or
        -not (Test-Path -LiteralPath $baselineReferenceHashes)) {
        throw 'The accepted release baseline is missing. Run accept_data_baseline.bat once (it now also snapshots RecognitionReferences).'
    }

    $currentManifestObject = Get-Content -LiteralPath $releaseManifest -Raw -Encoding UTF8 | ConvertFrom-Json
    $baselineManifestObject = Get-Content -LiteralPath $baselineManifest -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]$currentManifestObject.DataVersion -ne [string]$baselineManifestObject.DataVersion) {
        throw 'Data contains an unaccepted version. Create/test the data update and run accept_data_baseline.bat first.'
    }

    $currentCharactersHash = (Get-FileHash -LiteralPath $releaseCharacters -Algorithm SHA256).Hash
    $baselineCharactersHash = (Get-FileHash -LiteralPath $baselineCharacters -Algorithm SHA256).Hash
    if ($currentCharactersHash -ne $baselineCharactersHash) {
        throw 'characters.json contains unaccepted changes. Run create_data_update.bat and accept_data_baseline.bat first.'
    }

    $baselineHashArray = @(Get-Content -LiteralPath $baselineHashes -Raw -Encoding UTF8 | ConvertFrom-Json)
    $releaseImages = Join-Path $ProjectDataDirectory 'CharacterImages'
    foreach ($hashItem in $baselineHashArray) {
        $imagePath = Join-Path $releaseImages ([string]$hashItem.FileName)
        if (-not (Test-Path -LiteralPath $imagePath)) {
            throw ("Accepted image is missing: {0}" -f $hashItem.FileName)
        }
        $currentHash = (Get-FileHash -LiteralPath $imagePath -Algorithm SHA256).Hash
        if ($currentHash -ne [string]$hashItem.Sha256) {
            throw ("Image contains an unaccepted change: {0}" -f $hashItem.FileName)
        }
    }

    $releaseImageCount = @(Get-ChildItem -LiteralPath $releaseImages -File).Count
    if ($releaseImageCount -ne $baselineHashArray.Count) {
        throw 'CharacterImages contains unaccepted added or removed files.'
    }

    $baselineReferenceHashArray = @(Get-Content -LiteralPath $baselineReferenceHashes -Raw -Encoding UTF8 | ConvertFrom-Json)
    $releaseReferences = Join-Path $ProjectDataDirectory 'RecognitionReferences'
    foreach ($hashItem in $baselineReferenceHashArray) {
        $referencePath = Join-Path $releaseReferences ([string]$hashItem.RelativePath)
        if (-not (Test-Path -LiteralPath $referencePath)) {
            throw ("Accepted recognition reference is missing: {0}" -f $hashItem.RelativePath)
        }
        $currentHash = (Get-FileHash -LiteralPath $referencePath -Algorithm SHA256).Hash
        if ($currentHash -ne [string]$hashItem.Sha256) {
            throw ("Recognition reference contains an unaccepted change: {0}" -f $hashItem.RelativePath)
        }
    }

    $releaseReferenceCount = if (Test-Path -LiteralPath $releaseReferences) {
        @(Get-ChildItem -LiteralPath $releaseReferences -File -Recurse).Count
    } else { 0 }
    if ($releaseReferenceCount -ne $baselineReferenceHashArray.Count) {
        throw 'RecognitionReferences contains unaccepted added or removed files.'
    }

    Write-Host ("Accepted data version: {0}" -f $currentManifestObject.DataVersion) -ForegroundColor Green
    Write-Host ''
    Write-Host '[2/5] Building self-contained Windows x64 release...' -ForegroundColor Yellow
    $PublishArguments = @(
        'publish', $ProjectPath,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishSingleFile=false',
        '-p:PublishReadyToRun=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-o', $OutputDirectory
    )
    & $DotnetCommand.Path @PublishArguments
    if ($LASTEXITCODE -ne 0) { throw ("dotnet publish failed with exit code {0}." -f $LASTEXITCODE) }

    Write-Host ''
    Write-Host '[3/5] Removing development and temporary files...' -ForegroundColor Yellow
    Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -like '*.pdb' -or
            $_.Name -like '*.backup' -or
            $_.Name -like '*.tmp' -or
            $_.Name -like '*.seed.tmp'
        } |
        Remove-Item -Force -ErrorAction SilentlyContinue

    $WinX86Directory = Join-Path $OutputDirectory 'runtimes\win-x86'
    if (Test-Path -LiteralPath $WinX86Directory) { Remove-Item -LiteralPath $WinX86Directory -Recurse -Force }

    Write-Host ''
    Write-Host '[4/5] Creating portable ZIP...' -ForegroundColor Yellow
    Compress-Archive -Path (Join-Path $OutputDirectory '*') -DestinationPath $ZipPath -CompressionLevel Optimal -Force
    if (-not (Test-Path -LiteralPath $ZipPath)) { throw 'Portable ZIP was not created.' }

    Write-Host ''
    Write-Host '[5/5] Removing duplicated publish and build folders...' -ForegroundColor Yellow
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
    foreach ($buildDirectory in @('bin', 'obj')) {
        $buildPath = Join-Path $ProjectRoot $buildDirectory
        if (Test-Path -LiteralPath $buildPath) { Remove-Item -LiteralPath $buildPath -Recurse -Force }
    }

    $ZipSizeMb = [Math]::Round((Get-Item -LiteralPath $ZipPath).Length / 1MB, 2)
    Write-Host ''
    Write-Host '=============================================' -ForegroundColor Green
    Write-Host ' Publish completed successfully.' -ForegroundColor Green
    Write-Host '=============================================' -ForegroundColor Green
    Write-Host ("ZIP:  {0}" -f $ZipPath)
    Write-Host ("Size: {0} MB" -f $ZipSizeMb)
    Write-Host ''
    Write-Host 'The unpacked 300-400 MB folder and bin/obj were removed automatically.' -ForegroundColor Green
    Write-Host 'Share only the portable ZIP file.' -ForegroundColor Green
    Write-Host ''

    try { Start-Process explorer.exe -ArgumentList $PublishRoot } catch { }
    Wait-ForClose
    exit 0
}
catch {
    Write-Host ''
    Write-Host '=============================================' -ForegroundColor Red
    Write-Host ' Publish failed.' -ForegroundColor Red
    Write-Host '=============================================' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ''
    Write-Host ("Expected Publish path: {0}" -f $PublishRoot)
    Write-Host ''
    Wait-ForClose
    exit 1
}
