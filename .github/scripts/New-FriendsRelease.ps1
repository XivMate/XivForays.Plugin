[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^friends-v[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$')][string] $Tag,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $Repository,
    [Parameter(Mandatory)][string] $Package,
    [Parameter(Mandatory)][string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$version = $Tag.Substring('friends-v'.Length)
$packagePath = (Resolve-Path -LiteralPath $Package).Path
$pluginName = 'XivForays.Plugin'
$archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $manifestEntry = $archive.GetEntry("$pluginName.json")
    if ($null -eq $manifestEntry -or $null -eq $archive.GetEntry("$pluginName.dll")) {
        throw "The Dalamud package must contain $pluginName.json and $pluginName.dll at its root."
    }
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try {
        $manifest = $reader.ReadToEnd() | ConvertFrom-Json
    } finally {
        $reader.Dispose()
    }
} finally {
    $archive.Dispose()
}

if ($manifest.InternalName -ne $pluginName -or $manifest.AssemblyVersion -ne $version -or
    $manifest.DalamudApiLevel -ne 15) {
    throw 'The tag must match the packaged assembly version, and the manifest must target Dalamud API 15.'
}

$repositoryUrl = "https://github.com/$Repository"
$assetUrl = "$repositoryUrl/releases/download/$Tag/$pluginName.zip"
$template = @(Get-Content -Raw -LiteralPath "$PSScriptRoot/../../repo.json" | ConvertFrom-Json)
if ($template.Count -ne 1 -or $template[0].InternalName -ne $pluginName) {
    throw 'The repository listing must contain exactly one entry for XivForays.Plugin.'
}

$entry = $template[0]
foreach ($field in 'Author', 'Name', 'Description', 'Punchline', 'ApplicableVersion', 'AssemblyVersion', 'DalamudApiLevel') {
    $entry.$field = $manifest.$field
}
$entry.RepoUrl = $repositoryUrl
$entry.DownloadLinkInstall = $assetUrl
$entry.DownloadLinkUpdate = $assetUrl
$entry.LastUpdate = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($outputPath)
Copy-Item -LiteralPath $packagePath -Destination (Join-Path $outputPath "$pluginName.zip")
ConvertTo-Json -InputObject @($entry) -Depth 10 |
    Set-Content -LiteralPath (Join-Path $outputPath 'repo.json') -Encoding utf8NoBOM
