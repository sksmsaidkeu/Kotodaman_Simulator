$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$ProjectRoot = $PSScriptRoot
$ProjectPath = Join-Path $ProjectRoot 'KotodamanWordFinder.csproj'
$PublishDir = Join-Path $ProjectRoot 'Publish\app'
$ReleasesDir = Join-Path $ProjectRoot 'Releases'
$ProjectDataDirectory = Join-Path $ProjectRoot 'Data'
$LatestJsonPath = Join-Path $ProjectRoot 'latest.json'

$PackId = 'KotodamanWordFinder'
$Channel = 'win'
$RepoUrl = 'https://github.com/sksmsaidkeu/Kotodaman_Simulator'

function Wait-ForClose {
    try { [void](Read-Host 'Press Enter to close') }
    catch { Start-Sleep -Seconds 3 }
}

function Get-ToolPath([string]$Name) {
    $cmd = Get-Command "$Name.exe" -ErrorAction SilentlyContinue
    if ($null -eq $cmd) { $cmd = Get-Command $Name -ErrorAction SilentlyContinue }
    if ($null -eq $cmd) { throw ("{0} was not found on PATH." -f $Name) }
    return $cmd.Path
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

    Write-Host ''
    Write-Host '=============================================' -ForegroundColor Cyan
    Write-Host (" Kotodaman Word Finder v{0} Velopack Release" -f $AppVersion) -ForegroundColor Cyan
    Write-Host '=============================================' -ForegroundColor Cyan
    Write-Host ''

    $DotnetPath = Get-ToolPath 'dotnet'
    $VpkPath = Get-ToolPath 'vpk'
    $GhPath = Get-ToolPath 'gh'

    Write-Host '[1/6] Validating accepted release data...' -ForegroundColor Yellow
    $releaseCharacters = Join-Path $ProjectDataDirectory 'characters.json'
    $releaseManifest = Join-Path $ProjectDataDirectory 'data_manifest.json'
    $baselineRoot = Join-Path $ProjectRoot 'ReleaseTools\Baseline'
    $baselineManifest = Join-Path $baselineRoot 'data_manifest.json'
    if (-not (Test-Path -LiteralPath $releaseCharacters) -or -not (Test-Path -LiteralPath $releaseManifest)) {
        throw 'Data\characters.json or Data\data_manifest.json is missing.'
    }
    if (-not (Test-Path -LiteralPath $baselineManifest)) {
        throw 'The accepted release baseline is missing. Run accept_data_baseline.bat first.'
    }
    $currentManifestObject = Get-Content -LiteralPath $releaseManifest -Raw -Encoding UTF8 | ConvertFrom-Json
    $baselineManifestObject = Get-Content -LiteralPath $baselineManifest -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]$currentManifestObject.DataVersion -ne [string]$baselineManifestObject.DataVersion) {
        throw 'Data contains an unaccepted version. Run create_data_update.bat and accept_data_baseline.bat first.'
    }
    Write-Host ("Accepted data version: {0}" -f $currentManifestObject.DataVersion) -ForegroundColor Green

    Write-Host ''
    Write-Host '[2/6] Building self-contained Windows x64 release...' -ForegroundColor Yellow
    if (Test-Path -LiteralPath $PublishDir) { Remove-Item -LiteralPath $PublishDir -Recurse -Force }
    $PublishArguments = @(
        'publish', $ProjectPath,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishSingleFile=false',
        '-p:PublishReadyToRun=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-o', $PublishDir
    )
    & $DotnetPath @PublishArguments
    if ($LASTEXITCODE -ne 0) { throw ("dotnet publish failed with exit code {0}." -f $LASTEXITCODE) }

    Write-Host ''
    Write-Host '[3/6] Packing Velopack release (vpk pack)...' -ForegroundColor Yellow
    if (-not (Test-Path -LiteralPath $ReleasesDir)) { New-Item -ItemType Directory -Path $ReleasesDir -Force | Out-Null }
    $PackArguments = @(
        'pack',
        '-u', $PackId,
        '-v', $AppVersion,
        '-p', $PublishDir,
        '-e', 'KotodamanWordFinder.exe',
        '-i', (Join-Path $ProjectRoot 'Assets\AppIcon.ico'),
        '--packTitle', '코토다망 최장 단어 탐색기',
        '--packAuthors', 'KotodamanWordFinder',
        '--noPortable',
        '-o', $ReleasesDir
    )
    & $VpkPath @PackArguments
    if ($LASTEXITCODE -ne 0) { throw ("vpk pack failed with exit code {0}." -f $LASTEXITCODE) }

    $SetupExeName = "{0}-{1}-Setup.exe" -f $PackId, $Channel
    $SetupExePath = Join-Path $ReleasesDir $SetupExeName
    if (-not (Test-Path -LiteralPath $SetupExePath)) {
        throw ("Expected setup file was not produced: {0}" -f $SetupExePath)
    }

    Write-Host ''
    Write-Host '[4/6] Uploading release to GitHub (vpk upload github)...' -ForegroundColor Yellow
    $GithubToken = (& $GhPath auth token).Trim()
    if ([string]::IsNullOrWhiteSpace($GithubToken)) {
        throw 'Could not obtain a GitHub token from "gh auth token". Run "gh auth login" first.'
    }
    $Tag = "v{0}" -f $AppVersion
    $UploadArguments = @(
        'upload', 'github',
        '-o', $ReleasesDir,
        '--repoUrl', $RepoUrl,
        '--token', $GithubToken,
        '--publish',
        '--releaseName', ("코토다망 최장 단어 탐색기 v{0}" -f $AppVersion),
        '--tag', $Tag
    )
    & $VpkPath @UploadArguments
    if ($LASTEXITCODE -ne 0) { throw ("vpk upload github failed with exit code {0}." -f $LASTEXITCODE) }

    Write-Host ''
    Write-Host '[5/6] Writing latest.json manifest...' -ForegroundColor Yellow
    $SetupSha256 = (Get-FileHash -LiteralPath $SetupExePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $LatestJson = [ordered]@{
        version      = $AppVersion
        releaseTag   = $Tag
        releaseUrl   = "$RepoUrl/releases/tag/$Tag"
        setupUrl     = "$RepoUrl/releases/download/$Tag/$SetupExeName"
        setupSha256  = $SetupSha256
        publishedAt  = (Get-Date).ToUniversalTime().ToString('o')
    }
    ($LatestJson | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath $LatestJsonPath -Encoding UTF8
    Write-Host ("latest.json written: {0}" -f $LatestJsonPath) -ForegroundColor Green

    Write-Host ''
    Write-Host '[6/6] Committing and pushing latest.json...' -ForegroundColor Yellow
    $GitPath = Get-ToolPath 'git'
    & $GitPath -C $ProjectRoot add 'latest.json'
    & $GitPath -C $ProjectRoot commit -m ("release: v{0}" -f $AppVersion)
    if ($LASTEXITCODE -ne 0) { throw 'git commit for latest.json failed.' }
    & $GitPath -C $ProjectRoot push origin main
    if ($LASTEXITCODE -ne 0) { throw 'git push for latest.json failed.' }

    Write-Host ''
    Write-Host '=============================================' -ForegroundColor Green
    Write-Host ' Velopack release completed successfully.' -ForegroundColor Green
    Write-Host '=============================================' -ForegroundColor Green
    Write-Host ("Release:   {0}/releases/tag/{1}" -f $RepoUrl, $Tag)
    Write-Host ("Setup exe: {0}" -f $SetupExePath)
    Write-Host ''

    Wait-ForClose
    exit 0
}
catch {
    Write-Host ''
    Write-Host '=============================================' -ForegroundColor Red
    Write-Host ' Velopack release failed.' -ForegroundColor Red
    Write-Host '=============================================' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host ''
    Wait-ForClose
    exit 1
}
