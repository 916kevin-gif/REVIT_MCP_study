#Requires -Version 5.1

function Resolve-RevitAddinAssemblyTarget {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ManifestPath,

        [Parameter(Mandatory = $true)]
        [string]$TargetBase
    )

    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        throw "Revit add-in manifest not found: $ManifestPath"
    }

    $utf8Strict = New-Object System.Text.UTF8Encoding($false, $true)
    $manifestText = [System.IO.File]::ReadAllText(
        [System.IO.Path]::GetFullPath($ManifestPath),
        $utf8Strict
    )
    $assemblyMatches = [regex]::Matches(
        $manifestText,
        "<Assembly>\s*([^<]+?)\s*</Assembly>",
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    )

    if ($assemblyMatches.Count -ne 1) {
        throw "Manifest must contain exactly one <Assembly>: $ManifestPath"
    }

    $relativePath = $assemblyMatches[0].Groups[1].Value.Trim().Replace([char]47, [char]92)
    if ([string]::IsNullOrWhiteSpace($relativePath)) {
        throw "Manifest <Assembly> cannot be empty."
    }
    if ([System.IO.Path]::IsPathRooted($relativePath)) {
        throw "Manifest <Assembly> must be a relative path: $relativePath"
    }

    $segments = @($relativePath -split "[\\/]")
    $invalidSegments = @(
        $segments | Where-Object {
            [string]::IsNullOrWhiteSpace($_) -or $_ -eq "." -or $_ -eq ".."
        }
    )
    if ($invalidSegments.Count -gt 0) {
        throw "Manifest <Assembly> contains an unsafe path segment: $relativePath"
    }
    if ([System.IO.Path]::GetExtension($relativePath) -ine ".dll") {
        throw "Manifest <Assembly> must reference a DLL: $relativePath"
    }

    $targetBaseFull = [System.IO.Path]::GetFullPath($TargetBase)
    $targetFull = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine($targetBaseFull, $relativePath)
    )
    $basePrefix = $targetBaseFull.TrimEnd([char[]]@([char]92, [char]47)) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $targetFull.StartsWith(
        $basePrefix,
        [System.StringComparison]::OrdinalIgnoreCase
    )) {
        throw "Manifest <Assembly> escapes the deployment root: $relativePath"
    }

    [PSCustomObject]@{
        RelativePath = $relativePath
        FullPath = $targetFull
        DirectoryPath = [System.IO.Path]::GetDirectoryName($targetFull)
    }
}
