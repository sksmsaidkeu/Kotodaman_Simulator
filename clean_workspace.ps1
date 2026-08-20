$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$ProjectRoot = $PSScriptRoot
$removed = New-Object System.Collections.Generic.List[string]

try {
    foreach ($relativePath in @('bin', 'obj')) {
        $path = Join-Path $ProjectRoot $relativePath
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
            $removed.Add($relativePath)
        }
    }

    $publishRoot = Join-Path $ProjectRoot 'Publish'
    if (Test-Path -LiteralPath $publishRoot) {
        Get-ChildItem -LiteralPath $publishRoot -Directory | ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force
            $removed.Add(('Publish\\' + $_.Name))
        }
    }

    Get-ChildItem -LiteralPath $ProjectRoot -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -like '*.tmp' -or
            $_.Name -like '*.seed.tmp' -or
            $_.Name -like '*.update.tmp'
        } |
        ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Force
        }

    Write-Host ''
    if ($removed.Count -eq 0) {
        Write-Host 'No build artifacts needed cleaning.' -ForegroundColor Green
    }
    else {
        Write-Host 'Workspace cleanup completed.' -ForegroundColor Green
        foreach ($item in $removed) { Write-Host ('  Removed: ' + $item) }
    }
    Write-Host 'Portable ZIP files in Publish were preserved.'
}
catch {
    Write-Host ''
    Write-Host 'Workspace cleanup failed.' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
