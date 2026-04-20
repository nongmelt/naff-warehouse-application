param(
    [string]$VideoFolder = "$env:USERPROFILE\Videos\Warehouse",
    [int]$KeepDays = 15,
    [string]$LogFile,
    [switch]$WhatIf
)

# --- LOG SETUP ---
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $LogFile) {
    $logDir = Join-Path $scriptDir "logs\cleanup"
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    $dateStr = Get-Date -Format "dd-MM-yyyy"
    $LogFile = Join-Path $logDir "$env:COMPUTERNAME`_$dateStr.log"
}

function Write-Log($msg) {
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $msg"
    Add-Content -LiteralPath $LogFile -Value $line
}

# --- VALIDATE FOLDER ---
if (-not (Test-Path -LiteralPath $VideoFolder)) {
    Write-Error "[ERROR] Folder not found: $VideoFolder"
    Write-Log "[ERROR] Folder not found: $VideoFolder"
    exit 1
}

Write-Host "Starting cleanup for $env:COMPUTERNAME..."
Write-Host "From folder: $VideoFolder"
Write-Log "Starting cleanup for $env:COMPUTERNAME (keeping last $KeepDays days)"

# --- DELETE MP4s OLDER THAN KeepDays ---
$cut = (Get-Date).AddDays(-$KeepDays)
$c = 0; $b = 0; $e = 0

Get-ChildItem -LiteralPath $VideoFolder -Recurse -Filter '*.mp4' |
    Where-Object { $_.CreationTime -lt $cut } |
    ForEach-Object {
        try {
            $size = $_.Length
            $age  = [int]((Get-Date) - $_.CreationTime).TotalDays
            if ($WhatIf) {
                Write-Host "[WHATIF] Would delete: $($_.FullName) ($size bytes, $age days old)"
            } else {
                Remove-Item -LiteralPath $_.FullName -Force
                Write-Log "[DELETED] $($_.FullName) ($size bytes, $age days old)"
            }
            $c++; $b += $size
        } catch {
            Write-Log "[ERROR] $($_.FullName): $_"
            $e++
        }
    }

# --- REMOVE EMPTY FOLDERS ---
Get-ChildItem -LiteralPath $VideoFolder -Recurse -Directory |
    Sort-Object FullName -Descending |
    ForEach-Object {
        if (-not (Get-ChildItem -LiteralPath $_.FullName)) {
            Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
        }
    }

# --- SUMMARY ---
$mb = [math]::Round($b / 1MB, 2)

Write-Host ""
Write-Host "----------------------------------------"
Write-Host " Cleanup Summary for $env:COMPUTERNAME"
Write-Host "----------------------------------------"
Write-Host " Files deleted : $c"
Write-Host " Space freed   : $mb MB"
Write-Host " Errors        : $e"
Write-Host "----------------------------------------"

Write-Log "--- SUMMARY --- Files deleted: $c | Space freed: $mb MB ($b bytes) | Errors: $e"
Write-Log "Cleanup complete"

if ($e -gt 0) {
    Write-Warning "Completed with $e error(s). Check log: $LogFile"
    exit 2
}

exit 0
