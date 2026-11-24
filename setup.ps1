[CmdletBinding()]
param(
    [switch]$Register,
    [string]$ConfigPath = (Join-Path $env:USERPROFILE '.gemini\antigravity\mcp_config.json')
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$nodeCommand = (Get-Command node -ErrorAction Stop).Source
Get-Command dotnet -ErrorAction Stop | Out-Null
Push-Location -LiteralPath $projectRoot
try {
    & npm.cmd ci --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed.' }
    & dotnet build native/AgyComputerUse.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Native helper build failed. Install .NET SDK 10 with the Windows Desktop runtime.' }
    & $nodeCommand (Join-Path $projectRoot 'scripts\build-plugin.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'Local plugin packaging failed.' }
    $server = @{ command = $nodeCommand; args = @((Join-Path $projectRoot 'src\server.mjs')) }
    $example = @{ mcpServers = @{ 'agy-computer-use' = $server } }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText((Join-Path $projectRoot 'antigravity.mcp.json'), ($example | ConvertTo-Json -Depth 20), $utf8)
    if ($Register) {
        $config = @{}
        if (Test-Path -LiteralPath $ConfigPath) {
            $existingText = [IO.File]::ReadAllText($ConfigPath)
            if (-not [string]::IsNullOrWhiteSpace($existingText)) {
                $existingObject = $existingText | ConvertFrom-Json
                if ($existingObject -isnot [PSCustomObject]) { throw 'MCP configuration must be a JSON object; no changes were made.' }
                foreach ($property in $existingObject.PSObject.Properties) { $config[$property.Name] = $property.Value }
            }
        }
        $servers = @{}
        if ($config.ContainsKey('mcpServers')) {
            if ($null -eq $config['mcpServers'] -or $config['mcpServers'] -isnot [PSCustomObject]) { throw 'mcpServers must be a JSON object; no changes were made.' }
            foreach ($property in $config['mcpServers'].PSObject.Properties) { $servers[$property.Name] = $property.Value }
        }
        $servers['agy-computer-use'] = $server
        $config['mcpServers'] = $servers
        $resolvedConfig = [IO.Path]::GetFullPath($ConfigPath)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedConfig)) | Out-Null
        [IO.File]::WriteAllText($resolvedConfig, ($config | ConvertTo-Json -Depth 100), $utf8)
        Write-Host "Registered agy-computer-use in $resolvedConfig. Refresh MCP servers in Antigravity."
    } else {
        Write-Host 'Ready. Merge antigravity.mcp.json into your MCP configuration, or run .\setup.ps1 -Register.'
    }
} finally { Pop-Location }
