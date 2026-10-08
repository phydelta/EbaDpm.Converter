<#
.SYNOPSIS
    Sets up the data directory used by the data-dependent tests (Data/ at the repository root by
    default).

.DESCRIPTION
    Creates the data directory described in docs/test-data.md, under the exact names the tests
    look up:
    - the EBA DPM Access databases and the EBA Annotated Table Layouts, downloaded from the EBA;
    - the reference SQLite exports, extracted from tests/reference-exports/ in this repository.

    The script is idempotent: an item already in place is left untouched, unless -Force is
    given. Downloads go to a temporary folder that is removed at the end.

    Note: once the data directory exists, the data-dependent tests are no longer skipped. A
    test whose file is missing fails with a message naming it.

.PARAMETER DataDirectory
    Target directory. Defaults to the EBADPM_TEST_DATA environment variable when it names a
    directory path, otherwise Data/ at the repository root.

.PARAMETER Force
    Download and replace items that already exist.

.PARAMETER SkipDatabases
    Only fetch the table layouts (a few tens of MB instead of about 500 MB of downloads).

.EXAMPLE
    ./scripts/Initialize-TestData.ps1

.EXAMPLE
    ./scripts/Initialize-TestData.ps1 -DataDirectory D:\ebadpm-data
#>
[CmdletBinding()]
param(
    [string] $DataDirectory,
    [switch] $Force,
    [switch] $SkipDatabases
)

$ErrorActionPreference = 'Stop'
# The progress bar slows Invoke-WebRequest down by an order of magnitude on Windows PowerShell.
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repositoryRoot = Split-Path -Parent $PSScriptRoot

if (-not $DataDirectory) {
    # 'none' (or any value that is not a path) is the fast-loop convention: ignore it.
    $fromEnvironment = $env:EBADPM_TEST_DATA
    $DataDirectory = if ($fromEnvironment -and [System.IO.Path]::IsPathRooted($fromEnvironment)) {
        $fromEnvironment
    } else {
        Join-Path $repositoryRoot 'Data'
    }
}
$DataDirectory = [System.IO.Path]::GetFullPath($DataDirectory)

$referenceExports = Join-Path (Join-Path $repositoryRoot 'tests') 'reference-exports'

# Where each item comes from (a 'Url' to download, or a 'Source' zip in this repository) and
# where it goes. 'Entry' is the file inside the zip for a single-file item; 'Target' is the name
# the tests expect (tests/.../Schema/RepoPaths.cs).
$downloads = @(
    [pscustomobject]@{
        Kind   = 'Database'
        Target = 'DPM 1.0 Database_v4_1_20250709.accdb'
        Entry  = 'DPM 1.0 Database_v4_1_20250709.accdb'
        Source = $null
        Url    = 'https://www.eba.europa.eu/sites/default/files/2025-07/cf160041-aa49-41d3-af2b-d7e840158d65/DPM%201.0%20Database_v4_1_20250709.accdb_.zip'
    }
    [pscustomobject]@{
        Kind   = 'Database'
        Target = 'DPM2 Database_v 4_2_1.accdb'
        Entry  = 'DPM2 Database_v 4_2_1.accdb'
        Source = $null
        Url    = 'https://www.eba.europa.eu/sites/default/files/2026-02/ad0d2577-a1eb-4826-a249-a1f7701c6796/DPM2%20Database_v_4_2_1.zip'
    }
    [pscustomobject]@{
        Kind   = 'Database'
        Target = 'DPM2 Database_v 4_3_20260622.accdb'
        Entry  = 'DPM2 Database_v 4_3_20260622.accdb'
        Source = $null
        Url    = 'https://ebprstaewspublic01.blob.core.windows.net/public/tools-prod/documents/Big_Files/files/Reporting%20framework%204.3/DPM2%20Database_v%204_3.zip'
    }
    [pscustomobject]@{
        Kind   = 'Layouts'
        Target = '3.2 table layouts'
        Entry  = $null
        Source = $null
        Url    = 'https://www.eba.europa.eu/sites/default/files/2023-11/7e727bbe-83e4-4faa-938e-7580a29d5482/3.2%20table%20layouts.zip'
    }
    [pscustomobject]@{
        Kind   = 'Layouts'
        Target = '4.2 table layouts'
        Entry  = $null
        Source = $null
        Url    = 'https://www.eba.europa.eu/sites/default/files/2026-02/c92d5e2b-bcd7-4767-ab5b-cdf0b1737368/260106%20Annotated%20templates_with_finpre9dp_4.2.1.zip'
    }
    [pscustomobject]@{
        Kind   = 'Layouts'
        Target = '4.3 table layouts'
        Entry  = $null
        Source = $null
        Url    = 'https://www.eba.europa.eu/sites/default/files/2026-07/71f710c3-111a-4976-9731-bc9bdaa2036c/c.%20DPM%20Table%20Layouts%20and%20data%20point%20categorisation.zip'
    }
)

# The reference SQLite exports are not published by the EBA: they ship with the repository.
foreach ($name in 'EBA_3.2_phase_1.db', 'EBA_4.0_ERRATA_5.db', 'EBA_4.2_Hotfix.db') {
    $downloads += [pscustomobject]@{
        Kind   = 'Reference'
        Target = $name
        Entry  = $name
        Source = Join-Path $referenceExports "$name.zip"
    }
}

function Test-ItemPresent([pscustomobject] $item) {
    $path = Join-Path $DataDirectory $item.Target
    if ($item.Entry) {
        return Test-Path -LiteralPath $path -PathType Leaf
    }
    return (Test-Path -LiteralPath $path -PathType Container) -and
        @(Get-ChildItem -LiteralPath $path -Filter '*.xlsx' -Recurse -File).Count -gt 0
}

function Install-Item([pscustomobject] $item, [string] $workDirectory) {
    if ($item.Source) {
        $zipPath = $item.Source
    } else {
        $zipPath = Join-Path $workDirectory ([guid]::NewGuid().ToString() + '.zip')
        Write-Host "  downloading $([uri]::UnescapeDataString(($item.Url -split '/')[-1]))"
        Invoke-WebRequest -Uri $item.Url -OutFile $zipPath -UseBasicParsing
    }

    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $target = Join-Path $DataDirectory $item.Target
        if ($item.Entry) {
            $entry = $zip.Entries | Where-Object { $_.FullName -eq $item.Entry }
            if (-not $entry) {
                $found = ($zip.Entries | ForEach-Object FullName) -join ', '
                throw "The package no longer contains '$($item.Entry)' (found: $found). The EBA may have replaced the edition; see docs/test-data.md."
            }
            # Extract next to the target and rename, so an interrupted run leaves no half file.
            $partial = "$target.partial"
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $partial, $true)
            Move-Item -LiteralPath $partial -Destination $target -Force
        } else {
            if (Test-Path -LiteralPath $target) {
                Remove-Item -LiteralPath $target -Recurse -Force
            }
            [System.IO.Compression.ZipFileExtensions]::ExtractToDirectory($zip, $target)
        }
    } finally {
        $zip.Dispose()
    }
}

Write-Host "Data directory: $DataDirectory"

$aceInstalled = Test-Path 'Registry::HKEY_CLASSES_ROOT\Microsoft.ACE.OLEDB.16.0'
if (-not $aceInstalled) {
    Write-Warning ('Microsoft.ACE.OLEDB.16.0 is not registered. The data tests need the 64-bit ' +
        'Microsoft Access Database Engine to open the .accdb files.')
}

New-Item -ItemType Directory -Force -Path $DataDirectory | Out-Null
$workDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('ebadpm-data-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workDirectory | Out-Null

$failures = @()
try {
    foreach ($item in $downloads) {
        if ($SkipDatabases -and $item.Kind -eq 'Database') { continue }
        if ((Test-ItemPresent $item) -and -not $Force) {
            Write-Host "[present]  $($item.Target)"
            continue
        }
        Write-Host "[install]  $($item.Target)"
        try {
            Install-Item $item $workDirectory
        } catch {
            $failures += "$($item.Target): $($_.Exception.Message)"
            Write-Warning "  failed: $($_.Exception.Message)"
        }
    }
} finally {
    Remove-Item -LiteralPath $workDirectory -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host 'Summary'
$missingRequired = @()
foreach ($item in $downloads) {
    $present = Test-ItemPresent $item
    if (-not $present) { $missingRequired += $item.Target }
    Write-Host ('  {0,-8} {1}' -f ($(if ($present) { 'ok' } else { 'MISSING' })), $item.Target)
}

if ($missingRequired.Count -gt 0) {
    Write-Host ''
    Write-Host (('{0} item(s) are missing; the data tests that use them will fail until they ' +
        'are in place. See docs/test-data.md.') -f $missingRequired.Count)
}
if ($failures.Count -gt 0) {
    Write-Host ''
    $failures | ForEach-Object { Write-Host "  error: $_" }
    exit 1
}
