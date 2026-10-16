param(
    [string]$GameDirectory = '',
    [string]$AnchorChainDirectory = '',
    [switch]$Test,
    [switch]$InstallLocal
)
$ErrorActionPreference = 'Stop'
$taskDotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path $taskDotnet)) { throw 'A .NET SDK is required.' }
$taskProperties = @('-p:UseSharedCompilation=false')
if ($GameDirectory) { $taskProperties += "-p:SeaPowerDir=$GameDirectory" }
if ($AnchorChainDirectory) { $taskProperties += "-p:AnchorChainDir=$AnchorChainDirectory" }
$taskHome = Join-Path $PSScriptRoot 'artifacts\dotnet-home'
New-Item -ItemType Directory -Force $taskHome | Out-Null
function Invoke-TaskProcess([string]$TaskExecutable, [string[]]$TaskArguments) {
    $taskStartInfo = New-Object System.Diagnostics.ProcessStartInfo
    $taskStartInfo.FileName = $TaskExecutable
    $taskStartInfo.Arguments = ($TaskArguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $taskStartInfo.UseShellExecute = $false
    $taskStartInfo.CreateNoWindow = $true
    $taskStartInfo.RedirectStandardOutput = $true
    $taskStartInfo.RedirectStandardError = $true
    $taskStartInfo.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $taskStartInfo.StandardErrorEncoding = New-Object System.Text.UTF8Encoding($false)
    $taskStartInfo.EnvironmentVariables['DOTNET_CLI_HOME'] = $taskHome
    $taskStartInfo.EnvironmentVariables['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $taskStartInfo.EnvironmentVariables['DOTNET_CLI_UI_LANGUAGE'] = 'en'
    $taskStartInfo.EnvironmentVariables['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
    $taskStartInfo.EnvironmentVariables['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
    $taskStartInfo.EnvironmentVariables['DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE'] = 'true'
    $taskProcess = [System.Diagnostics.Process]::Start($taskStartInfo)
    $taskOutput = $taskProcess.StandardOutput.ReadToEndAsync()
    $taskErrors = $taskProcess.StandardError.ReadToEndAsync()
    $taskProcess.WaitForExit()
    if ($taskOutput.Result) { Write-Output $taskOutput.Result.TrimEnd() }
    if ($taskErrors.Result) { Write-Output $taskErrors.Result.TrimEnd() }
    if ($taskProcess.ExitCode -ne 0) { throw "$TaskExecutable exited with $($taskProcess.ExitCode)." }
}
function Invoke-TaskDotnet([string[]]$TaskArguments) { Invoke-TaskProcess $taskDotnet $TaskArguments }
Invoke-TaskDotnet (@('build', (Join-Path $PSScriptRoot 'src\HarmonyStartupOptimizer.csproj'), '-c', 'Release', '--nologo', '--verbosity', 'minimal') + $taskProperties)
if ($Test) {
    Invoke-TaskDotnet (@('build', (Join-Path $PSScriptRoot 'tests\HarmonyStartupOptimizer.Tests.csproj'), '-c', 'Release', '--nologo', '--verbosity', 'minimal') + $taskProperties)
    Invoke-TaskDotnet @((Join-Path $PSScriptRoot 'tests\bin\Release\net10.0\HarmonyStartupOptimizer.Tests.dll'))
    Invoke-TaskDotnet (@('build', (Join-Path $PSScriptRoot 'tests\MonoSmoke\HarmonyStartupOptimizer.MonoSmoke.csproj'), '-c', 'Release', '--nologo', '--verbosity', 'minimal') + $taskProperties)
}
if (-not $GameDirectory -or -not $AnchorChainDirectory) {
    $taskLocalPath = Join-Path $PSScriptRoot 'Directory.Build.local.props'
    if (-not (Test-Path $taskLocalPath)) { throw 'Set local game paths or pass both directory arguments.' }
    [xml]$taskLocal = Get-Content $taskLocalPath
    if (-not $GameDirectory) { $GameDirectory = [string]$taskLocal.Project.PropertyGroup.SeaPowerDir }
    if (-not $AnchorChainDirectory) { $AnchorChainDirectory = [string]$taskLocal.Project.PropertyGroup.AnchorChainDir }
}
if ($Test) {
    $taskMonoOptions = @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'tools\test-mono.ps1'),'-GameDirectory',$GameDirectory,'-AnchorChainDirectory',$AnchorChainDirectory)
    Invoke-TaskProcess (Join-Path $PSHOME 'powershell.exe') $taskMonoOptions
}
$taskPackage = Join-Path $PSScriptRoot 'dist\HarmonyStartupOptimizer'
New-Item -ItemType Directory -Force $taskPackage | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'src\bin\Release\netstandard2.1\HarmonyStartupOptimizer.dll') $taskPackage -Force
Copy-Item (Join-Path $PSScriptRoot 'package\_info.ini') $taskPackage -Force
Copy-Item (Join-Path $PSScriptRoot 'README.md') $taskPackage -Force
Write-Output "Package: $taskPackage"
if ($InstallLocal) {
    if (-not (Test-Path (Join-Path $GameDirectory 'Sea Power.exe'))) { throw 'Sea Power.exe was not found.' }
    $taskGameProcesses = Get-Process -Name 'Sea Power' -ErrorAction SilentlyContinue
    if ($taskGameProcesses) { throw 'Close Sea Power before installing the mod.' }
    $taskDestination = Join-Path $GameDirectory 'Sea Power_Data\StreamingAssets\HarmonyStartupOptimizer'
    New-Item -ItemType Directory -Force $taskDestination | Out-Null
    Get-ChildItem $taskPackage -File | ForEach-Object { Copy-Item $_.FullName (Join-Path $taskDestination $_.Name) -Force }
    Write-Output "Installed: $taskDestination"
    Write-Output 'Enable Harmony Startup Optimizer in the mod manager, then restart Sea Power.'
}
