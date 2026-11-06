#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Windows', 'Switch', 'Native', 'All')]
    [string[]]$Target = @('Windows'),
    [string]$PathsFile = 'local.paths.json',
    [string]$OutputRoot = 'artifacts/Local',
    [ValidateRange(1, 64)][int]$Jobs = 4,
    [switch]$SkipUiCheck,
    [switch]$FrameworkDependent,
    [switch]$AllowNetwork,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

function Resolve-ProjectPath([string]$Value) {
    return [IO.Path]::GetFullPath($Value, $taskRoot)
}

function Get-LocalTool([object]$Paths, [string]$Name) {
    $taskValue = [string]$Paths.$Name
    if ([string]::IsNullOrWhiteSpace($taskValue)) { throw "Local paths configuration is missing '$Name'." }
    $taskPath = Resolve-ProjectPath $taskValue
    if (!(Test-Path -LiteralPath $taskPath -PathType Leaf)) { throw "Configured tool '$Name' does not exist: $taskPath" }
    return $taskPath
}

function Invoke-LocalTool([string]$File, [string[]]$Arguments, [hashtable]$Environment = @{}) {
    $taskStart = [Diagnostics.ProcessStartInfo]::new()
    $taskStart.FileName = $File
    $taskStart.WorkingDirectory = $taskRoot
    $taskStart.UseShellExecute = $false
    $taskStart.CreateNoWindow = $true
    $taskStart.RedirectStandardOutput = $true
    $taskStart.RedirectStandardError = $true
    foreach ($taskArgument in $Arguments) { $taskStart.ArgumentList.Add($taskArgument) }
    foreach ($taskKey in $Environment.Keys) { $taskStart.Environment[$taskKey] = [string]$Environment[$taskKey] }
    $taskProcess = [Diagnostics.Process]::Start($taskStart)
    try {
        # Drain stderr concurrently so a full error pipe cannot block the child.
        $taskError = $taskProcess.StandardError.ReadToEndAsync()
        while (!$taskProcess.StandardOutput.EndOfStream) { Write-Output $taskProcess.StandardOutput.ReadLine() }
        $taskProcess.WaitForExit()
        $taskErrorText = $taskError.GetAwaiter().GetResult()
        if ($taskErrorText) { Write-Output $taskErrorText.TrimEnd() }
        if ($taskProcess.ExitCode -ne 0) { throw "Local tool failed with exit code $($taskProcess.ExitCode): $File" }
    }
    finally { $taskProcess.Dispose() }
}

Push-Location -LiteralPath $taskRoot
try {
    $taskPathsFile = Resolve-ProjectPath $PathsFile
    if (!(Test-Path -LiteralPath $taskPathsFile -PathType Leaf)) { throw "Missing local paths configuration: $taskPathsFile" }
    $taskPaths = Get-Content -LiteralPath $taskPathsFile -Raw | ConvertFrom-Json
    if ($taskPaths.ProjectRoot) {
        $taskConfiguredRoot = (Resolve-ProjectPath ([string]$taskPaths.ProjectRoot)).TrimEnd('\', '/')
        if (![string]::Equals($taskConfiguredRoot, $taskRoot.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'ProjectRoot in the local configuration must match this script repository. Do not build an older source copy.'
        }
    }
    $taskOutput = Resolve-ProjectPath $OutputRoot
    $taskRootPrefix = $taskRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (!$taskOutput.StartsWith($taskRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'OutputRoot must be a directory inside this project root.'
    }
    $taskTargets = if ($Target -contains 'All') { @('Native', 'Windows', 'Switch') } else { @($Target | Select-Object -Unique) }
    $taskPython = Get-LocalTool $taskPaths 'Python'
    $taskDotnet = if ($taskTargets -contains 'Windows') { Get-LocalTool $taskPaths 'Dotnet' }
    $taskBash = $null
    $taskCxx = $null
    $taskChildEnvironment = @{}
    if ($taskTargets -contains 'Native' -or $taskTargets -contains 'Switch') {
        if (!$taskPaths.MsysRoot) { throw "Local paths configuration is missing 'MsysRoot'." }
        $taskMsys = Resolve-ProjectPath ([string]$taskPaths.MsysRoot)
        $taskMsysBin = Join-Path $taskMsys 'usr/bin'
        $taskBash = Join-Path $taskMsysBin 'bash.exe'
        $taskCxx = Join-Path $taskMsysBin 'g++.exe'
        $taskMsysTools = @($taskBash)
        if ($taskTargets -contains 'Native') { $taskMsysTools += $taskCxx }
        foreach ($taskTool in $taskMsysTools) {
            if (!(Test-Path -LiteralPath $taskTool -PathType Leaf)) { throw "Missing MSYS tool: $taskTool" }
        }
        $taskChildEnvironment.PATH = $taskMsysBin + [IO.Path]::PathSeparator + $env:PATH
        $taskChildEnvironment.MSYSTEM = 'MSYS'
    }
    if ($taskTargets -contains 'Switch') {
        if (!$taskPaths.DevkitPro) { throw "Local paths configuration is missing 'DevkitPro'." }
        $taskSdk = Resolve-ProjectPath ([string]$taskPaths.DevkitPro)
        foreach ($taskTool in @('devkitA64/bin/aarch64-none-elf-g++.exe', 'tools/bin/elf2nro.exe')) {
            if (!(Test-Path -LiteralPath (Join-Path $taskSdk $taskTool) -PathType Leaf)) { throw "Missing dedicated Switch SDK tool: $taskTool" }
        }
    }
    if ($ValidateOnly) {
        [pscustomobject]@{ ProjectRoot = $taskRoot; OutputRoot = $taskOutput; Targets = $taskTargets; Offline = !$AllowNetwork; RebuildSdk = $false }
        return
    }
    Write-Output "Project root: $taskRoot"
    Write-Output "Local build output: $taskOutput"
    foreach ($taskTarget in $taskTargets) {
        switch ($taskTarget) {
            'Windows' {
                $taskWindowsArguments = @{
                    Dotnet = $taskDotnet; Python = $taskPython
                    OutputDirectory = Join-Path $taskOutput 'Windows'
                    TestDirectory = Join-Path (Join-Path $taskOutput 'Tests') ('run-' + [Guid]::NewGuid().ToString('N'))
                    SkipUiCheck = $SkipUiCheck; FrameworkDependent = $FrameworkDependent
                }
                & (Join-Path $taskRoot 'scripts/build.ps1') @taskWindowsArguments
            }
            'Native' {
                $taskNative = Join-Path $taskOutput 'Native'
                New-Item -ItemType Directory -Path $taskNative -Force | Out-Null
                foreach ($taskSuite in @('native', 'argv')) {
                    $taskExecutable = Join-Path $taskNative ($taskSuite + '-tests.exe')
                    $taskCompile = @('-std=c++20', '-O1', '-g', '-Wall', '-Wextra', '-Wpedantic', '-Werror',
                        '-Icompanion/include', ('companion/tests/' + $taskSuite + '_tests.cpp'), '-o', $taskExecutable.Replace('\', '/'))
                    Invoke-LocalTool -File $taskCxx -Arguments $taskCompile -Environment $taskChildEnvironment
                    Invoke-LocalTool -File $taskExecutable -Arguments @() -Environment $taskChildEnvironment
                }
                Invoke-LocalTool -File $taskPython -Arguments @('scripts/test-cnmt.py', '--cxx', $taskCxx, '--output',
                    (Join-Path $taskNative 'Cnmt'), '--verify-regression') -Environment $taskChildEnvironment
            }
            'Switch' {
                $taskSwitchEnvironment = $taskChildEnvironment.Clone()
                $taskSwitchEnvironment.DEVKITPRO = $taskSdk
                $taskSwitchEnvironment.SWITCHDESK_PYTHON = $taskPython
                $taskSwitchEnvironment.SWITCHDESK_SKIP_SDK_BUILD = '1'
                $taskSwitchEnvironment.SWITCHDESK_OFFLINE = if ($AllowNetwork) { '0' } else { '1' }
                $taskSwitchEnvironment.SWITCHDESK_SOURCE_DIR = Join-Path $taskOutput 'companion-source'
                $taskSwitchEnvironment.SWITCHDESK_BUILD_DIR = Join-Path $taskOutput 'switch-build'
                $taskSwitchEnvironment.SWITCHDESK_OUTPUT_DIR = Join-Path $taskOutput 'Switch'
                $taskSwitchEnvironment.SWITCHDESK_BUILD_JOBS = [string]$Jobs
                Invoke-LocalTool -File $taskBash -Arguments @('scripts/build-companion.sh') -Environment $taskSwitchEnvironment
            }
        }
    }
}
finally { Pop-Location }
