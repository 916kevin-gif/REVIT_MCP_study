#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "addin-deployment-path.ps1")

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "revitmcp-addin-path-" + [guid]::NewGuid().ToString("N")
)
$manifestPath = Join-Path $testRoot "RevitMCP.addin"
$targetBase = Join-Path $testRoot "Addins\2024"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Write-TestManifest {
    param([string]$AssemblyPath)
    $xml = "<RevitAddIns><AddIn Type=`"Application`"><Assembly>$AssemblyPath</Assembly></AddIn></RevitAddIns>"
    [System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
    [System.IO.File]::WriteAllText($manifestPath, $xml, $utf8NoBom)
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Name)
    try {
        & $Action
    }
    catch {
        Write-Host "PASS $Name"
        return
    }
    throw "FAIL ${Name}: expected path rejection."
}

try {
    Write-TestManifest "RevitMCP\RevitMCP.dll"
    $nested = Resolve-RevitAddinAssemblyTarget -ManifestPath $manifestPath -TargetBase $targetBase
    $expectedNested = [System.IO.Path]::GetFullPath(
        (Join-Path $targetBase "RevitMCP\RevitMCP.dll")
    )
    if ($nested.FullPath -ne $expectedNested) {
        throw "FAIL nested path: $($nested.FullPath)"
    }
    Write-Host "PASS RevitMCP\RevitMCP.dll"

    Write-TestManifest "RevitMCP.dll"
    $root = Resolve-RevitAddinAssemblyTarget -ManifestPath $manifestPath -TargetBase $targetBase
    $expectedRoot = [System.IO.Path]::GetFullPath((Join-Path $targetBase "RevitMCP.dll"))
    if ($root.FullPath -ne $expectedRoot) {
        throw "FAIL root path: $($root.FullPath)"
    }
    Write-Host "PASS RevitMCP.dll"

    Write-TestManifest "..\outside.dll"
    Assert-Throws {
        Resolve-RevitAddinAssemblyTarget -ManifestPath $manifestPath -TargetBase $targetBase
    } "reject parent traversal"

    Write-TestManifest "C:\outside.dll"
    Assert-Throws {
        Resolve-RevitAddinAssemblyTarget -ManifestPath $manifestPath -TargetBase $targetBase
    } "reject absolute path"
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
