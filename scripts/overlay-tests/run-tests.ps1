$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$OutputEncoding = [Console]::OutputEncoding
$testRoot = $PSScriptRoot
$repo = [IO.Path]::GetFullPath((Join-Path $testRoot '../..'))
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$artifacts = Join-Path $testRoot 'artifacts'
$dataRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'LiveSub'
$settings = Join-Path $dataRoot 'settings.json'
$log = Join-Path $dataRoot 'live.log'
$resultPath = Join-Path $testRoot 'results.txt'
$oldEnvironment = @{}
$exitCode = 1
$guardFailures = 0

function Get-ReadOnlyHash([string] $Path) {
    if (-not [IO.File]::Exists($Path)) { return 'MISSING' }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '') }
    finally { $sha.Dispose(); $stream.Dispose() }
}

function Get-ReadOnlyInventory([string[]] $Roots) {
    foreach ($root in $Roots) {
        if (-not [IO.Directory]::Exists($root)) { "MISSING|$root"; continue }
        Get-ChildItem -LiteralPath $root -Force -Recurse | Sort-Object FullName | ForEach-Object {
            if ($_.PSIsContainer) { "DIR|$($_.FullName)" }
            else { "FILE|$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" }
        }
    }
}

function Write-Result([string] $Text) {
    Write-Output $Text
    [IO.File]::AppendAllText($resultPath, $Text + [Environment]::NewLine, [Text.Encoding]::UTF8)
}

$settingsBefore = Get-ReadOnlyHash $settings
$logBefore = Get-ReadOnlyHash $log
$protectedTrees = @((Join-Path $repo 'src'), (Join-Path $repo 'models'), (Join-Path $dataRoot 'models'))
if ($env:LIVESUB_MODELS) { $protectedTrees += [IO.Path]::GetFullPath($env:LIVESUB_MODELS) }
$treeBefore = @(Get-ReadOnlyInventory $protectedTrees)
$treeBefore | Set-Content -LiteralPath (Join-Path $testRoot 'protected-tree.before.txt') -Encoding UTF8
$modelConfigPaths = @((Join-Path $dataRoot 'models.json'), (Join-Path $dataRoot 'customModels.json'))
$modelConfigBefore = @($modelConfigPaths | ForEach-Object { Get-ReadOnlyHash $_ })
"RUN started=$([DateTime]::Now.ToString('o'))" | Set-Content -LiteralPath $resultPath -Encoding UTF8
Write-Result "GUARD settings before SHA256=$settingsBefore; path=$settings"
Write-Result "GUARD log before SHA256=$logBefore; path=$log"

try {
    # All generated artifacts, CLI state, downloads and temporary files stay under this test directory.
    $environment = @{
        DOTNET_CLI_HOME = (Join-Path $testRoot '.cli')
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        DOTNET_NOLOGO = '1'
        DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
        NUGET_PACKAGES = (Join-Path $testRoot 'packages')
        NUGET_FALLBACK_PACKAGES = (Join-Path $env:USERPROFILE '.nuget/packages')
        NUGET_HTTP_CACHE_PATH = (Join-Path $testRoot '.nuget-http')
        NUGET_SCRATCH = (Join-Path $testRoot '.nuget-scratch')
        TEMP = (Join-Path $testRoot 'temp')
        TMP = (Join-Path $testRoot 'temp')
    }
    foreach ($name in $environment.Keys) {
        $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
    }
    foreach ($folder in @($artifacts, $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:NUGET_HTTP_CACHE_PATH, $env:NUGET_SCRATCH, $env:TEMP)) {
        [IO.Directory]::CreateDirectory($folder) | Out-Null
    }

    # WPF can create a temporary .csproj beside the referenced project, even with --artifacts-path.
    # Build an unmodified, byte-verified snapshot; never build or write the original project in src.
    $sourceRoot = Join-Path $repo 'src/LiveSub'
    $copyRoot = Join-Path $testRoot 'product-source/LiveSub'
    $sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -File -Recurse | Where-Object {
        $relative = $_.FullName.Substring($sourceRoot.Length + 1)
        $relative -notmatch '^(bin|obj)[\\/]'
    })
    foreach ($file in $sourceFiles) {
        $relative = $file.FullName.Substring($sourceRoot.Length + 1)
        $destination = Join-Path $copyRoot $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy($file.FullName, $destination, $true)
        if ((Get-ReadOnlyHash $file.FullName) -cne (Get-ReadOnlyHash $destination)) {
            throw "Product snapshot hash mismatch: $relative"
        }
    }
    Write-Result "PASS: $($sourceFiles.Count) product source/assets copied byte-for-byte into test-only snapshot"

    & $dotnet build (Join-Path $testRoot 'OverlayRegressionTests.csproj') --configuration Release --artifacts-path $artifacts --disable-build-servers '-p:NuGetAudit=false' '-p:UseSharedCompilation=false' 2>&1 |
        Tee-Object -FilePath (Join-Path $testRoot 'build-output.txt')
    $buildExitCode = $LASTEXITCODE
    Write-Result "BUILD exit=$buildExitCode"
    if ($buildExitCode -ne 0) { throw "Build failed with exit code $buildExitCode" }

    $testDll = Join-Path $artifacts 'bin/OverlayRegressionTests/release/OverlayRegressionTests.dll'
    & $dotnet $testDll $repo 2>&1 | ForEach-Object { Write-Result ([string]$_) }
    $exitCode = $LASTEXITCODE
    Write-Result "TEST PROCESS exit=$exitCode"
}
catch {
    Write-Result "FAIL: runner: $_"
    $exitCode = 1
}
finally {
    foreach ($name in $oldEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name], 'Process')
    }
    $settingsAfter = Get-ReadOnlyHash $settings
    Write-Result "GUARD settings after SHA256=$settingsAfter; path=$settings"
    if ($settingsBefore -ceq $settingsAfter) { Write-Result 'PASS: runner settings SHA256 unchanged across build and test' }
    else { Write-Result 'FAIL: settings changed; no restoration attempted'; $guardFailures++ }

    $logAfter = Get-ReadOnlyHash $log
    Write-Result "GUARD log after SHA256=$logAfter; path=$log"
    if ($logBefore -ceq $logAfter) { Write-Result 'PASS: original live.log SHA256 unchanged' }
    else { Write-Result 'FAIL: log changed during run (possible external activity); no restoration attempted'; $guardFailures++ }

    $treeAfter = @(Get-ReadOnlyInventory $protectedTrees)
    $treeAfter | Set-Content -LiteralPath (Join-Path $testRoot 'protected-tree.after.txt') -Encoding UTF8
    $differences = @(Compare-Object $treeBefore $treeAfter)
    if ($differences.Count -eq 0) { Write-Result 'PASS: src and model tree file paths/sizes/write timestamps unchanged' }
    else {
        Write-Result "FAIL: protected tree has $($differences.Count) inventory differences; no restoration attempted"
        Write-Result ($differences | Format-List InputObject, SideIndicator | Out-String)
        $guardFailures++
    }

    $modelConfigAfter = @($modelConfigPaths | ForEach-Object { Get-ReadOnlyHash $_ })
    if (@(Compare-Object $modelConfigBefore $modelConfigAfter).Count -eq 0) { Write-Result 'PASS: model selection/configuration SHA256 unchanged' }
    else { Write-Result 'FAIL: model configuration changed; no restoration attempted'; $guardFailures++ }

    if ($guardFailures -gt 0) { $exitCode = 1 }
    Write-Result "FINAL exit=$exitCode; guardFailures=$guardFailures"
}
exit $exitCode
