param([string]$OutputDirectory = 'artifacts\app', [switch]$NoRestore)

$ErrorActionPreference = 'Stop'
$taskProjectPath = Join-Path $PSScriptRoot 'src\GitManager.App\GitManager.App.csproj'
$taskPublishPath = [IO.Path]::GetFullPath($OutputDirectory, $PSScriptRoot)
$taskPublishArguments = @('--configuration', 'Release', '--self-contained', 'false', '--nologo', '-m:1', '-nodeReuse:false')
if ($NoRestore) { $taskPublishArguments += '--no-restore' }
dotnet publish $taskProjectPath @taskPublishArguments --output $taskPublishPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$taskMcpProjectPath = Join-Path $PSScriptRoot 'src\GitManager.Mcp\GitManager.Mcp.csproj'
dotnet publish $taskMcpProjectPath @taskPublishArguments --output (Join-Path $taskPublishPath 'mcp')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$taskMcpConfiguration = @{ mcpServers = @{ gitmanager = @{ command = (Join-Path $taskPublishPath 'mcp\GitManager.Mcp.exe'); args = @() } } }
$taskMcpConfiguration | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskPublishPath 'mcp-config.json') -Encoding UTF8
Write-Output (Join-Path $taskPublishPath 'GitManager.exe')
