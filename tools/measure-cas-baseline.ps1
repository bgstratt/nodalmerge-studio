<#
.SYNOPSIS
Measures CAS blob storage vs snapshot-node metadata in a NodalMerge Studio data root.

Read-only. Produces the before-numbers Phases 1/3/5 of
plans/cas-distribution-and-storage.md are judged against.

.PARAMETER DataRoot
The extension data root (see HostManager.resolveDataRoot): contains
data/blobs (CAS) and data/nodalmerge-nodes.db (SQLite node store).

.EXAMPLE
./tools/measure-cas-baseline.ps1 -DataRoot "$env:APPDATA\Code\User\globalStorage\<publisher>.<ext>"
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$DataRoot
)

$ErrorActionPreference = 'Stop'

$blobRoot = Join-Path $DataRoot 'data/blobs/blake3'
$dbPath = Join-Path $DataRoot 'data/nodalmerge-nodes.db'

# --- CAS blobs -------------------------------------------------------------
if (Test-Path $blobRoot) {
    $blobs = Get-ChildItem $blobRoot -File | Where-Object { $_.Name -match '^[0-9a-f]{64}(\.zst)?$' }
    $totalBytes = ($blobs | Measure-Object Length -Sum).Sum ?? 0
    Write-Host "== CAS blobs ($blobRoot)"
    Write-Host ("blob count : {0}" -f $blobs.Count)
    Write-Host ("total bytes: {0:N0} ({1:N1} MiB)" -f $totalBytes, ($totalBytes / 1MB))

    $buckets = [ordered]@{ '<1KB' = 0; '1-10KB' = 0; '10-100KB' = 0; '100KB-1MB' = 0; '>1MB' = 0 }
    foreach ($b in $blobs) {
        $k = if ($b.Length -lt 1KB) { '<1KB' }
        elseif ($b.Length -lt 10KB) { '1-10KB' }
        elseif ($b.Length -lt 100KB) { '10-100KB' }
        elseif ($b.Length -lt 1MB) { '100KB-1MB' }
        else { '>1MB' }
        $buckets[$k]++
    }
    Write-Host 'size histogram:'
    $buckets.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-10} {1}" -f $_.Key, $_.Value) }
}
else {
    Write-Warning "No CAS directory at $blobRoot"
    $totalBytes = 0
}

# --- Node store ------------------------------------------------------------
if (-not (Test-Path $dbPath)) {
    Write-Warning "No node store at $dbPath"
    return
}

# Prefer the sqlite3 CLI; fall back to Microsoft.Data.Sqlite from build output.
function Invoke-NodeQuery([string]$idPrefix) {
    $sql = "SELECT COUNT(*), COALESCE(SUM(LENGTH(payload)),0) FROM accepted_nodes WHERE node_id_hex LIKE '$idPrefix%';"
    $cli = Get-Command sqlite3 -ErrorAction SilentlyContinue
    if ($cli) {
        $row = & $cli.Source -readonly $dbPath $sql
        $parts = $row -split '\|'
        return [pscustomobject]@{ Count = [long]$parts[0]; Bytes = [long]$parts[1] }
    }
    # Fallback: load Microsoft.Data.Sqlite from any Studio build output.
    $dll = Get-ChildItem "$PSScriptRoot/../src" -Recurse -Filter Microsoft.Data.Sqlite.dll -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $dll) { throw 'Neither sqlite3 CLI nor a built Microsoft.Data.Sqlite.dll found. Install sqlite3 or build the Studio host.' }
    Add-Type -Path $dll.FullName
    $native = Get-ChildItem $dll.Directory -Recurse -Filter e_sqlite3.dll -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($native) { $env:PATH = "$($native.Directory);$env:PATH" }
    $conn = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$dbPath;Mode=ReadOnly")
    try {
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $sql
        $reader = $cmd.ExecuteReader()
        [void]$reader.Read()
        return [pscustomobject]@{ Count = $reader.GetInt64(0); Bytes = $reader.GetInt64(1) }
    }
    finally { $conn.Dispose() }
}

$snap = Invoke-NodeQuery 'studio:studio/repository-snapshot/v1:'
$ops = Invoke-NodeQuery 'studio:studio/repository-op/v1:'
$all = Invoke-NodeQuery 'studio:'

Write-Host "`n== Node store ($dbPath)"
Write-Host ("snapshot nodes : {0} rows, {1:N0} payload bytes ({2:N1} MiB)" -f $snap.Count, $snap.Bytes, ($snap.Bytes / 1MB))
Write-Host ("op nodes       : {0} rows, {1:N0} payload bytes ({2:N1} MiB)" -f $ops.Count, $ops.Bytes, ($ops.Bytes / 1MB))
Write-Host ("all studio     : {0} rows, {1:N0} payload bytes ({2:N1} MiB)" -f $all.Count, $all.Bytes, ($all.Bytes / 1MB))
if ($snap.Count -gt 0) {
    Write-Host ("avg snapshot-node bytes: {0:N0}" -f ($snap.Bytes / $snap.Count))
}
if ($totalBytes -gt 0) {
    Write-Host ("`nsnapshot-metadata : blob-content ratio = {0:P1}" -f ($snap.Bytes / $totalBytes))
}
