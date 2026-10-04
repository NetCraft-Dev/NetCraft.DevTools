# NetCraft 开发工具链一键安装
# 装三样 .NET 10 SDK dotnet new 项目模板 ncm 命令行工具
# 幂等 已经有的会跳过或就地更新 重复跑不会出问题
# 用法 ./tools/install.ps1

$ErrorActionPreference = "Stop"

$Channel = "10.0"
$TemplateId = "NetCraft.ModsProjectType"
$ToolId = "NetCraft.ModBuild.Tools"
$InstallRoot = Join-Path $env:USERPROFILE ".dotnet"
$ToolsDir = Join-Path $InstallRoot "tools"

#Invoke-Dotnet 跑一条 dotnet 命令 退出码非零直接终止脚本
#原生命令失败不会触发 $ErrorActionPreference 不自己看退出码就会一路跑到末尾报成功
function Invoke-Dotnet([string[]]$Command) {
    & dotnet @Command
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Command -join ' ') failed with exit code $LASTEXITCODE"
    }
}

#Test-Command 命令在不在 PATH 上
function Test-Command([string]$Name) {
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

#Test-Sdk 本机有没有指定大版本的 SDK
function Test-Sdk([string]$Major) {
    if (-not (Test-Command "dotnet")) { return $false }
    $sdks = & dotnet --list-sdks 2>$null
    return [bool]($sdks | Where-Object { $_ -match "^$Major\." })
}

#Install-Sdk 走官方脚本把 SDK 装进用户目录 不动系统级那份
function Install-Sdk {
    $script = Join-Path $env:TEMP "dotnet-install.ps1"
    Write-Host "downloading dotnet-install.ps1"
    Invoke-WebRequest "https://dot.net/v1/dotnet-install.ps1" -OutFile $script -UseBasicParsing
    Write-Host "installing .NET SDK $Channel into $InstallRoot"
    & $script -Channel $Channel -InstallDir $InstallRoot -NoPath
    if ($LASTEXITCODE -ne 0) { throw "dotnet-install.ps1 failed with exit code $LASTEXITCODE" }
    Remove-Item $script -Force
}

Write-Host "==> checking .NET SDK"

if (Test-Sdk "10") {
    Write-Host "found SDK $(& dotnet --version)"
}
else {
    Install-Sdk
    $env:PATH = "$InstallRoot;$env:PATH"
    if (-not (Test-Sdk "10")) { throw ".NET 10 SDK still missing after install" }
    Write-Host "installed SDK $(& dotnet --version)"
}

#工具目录也挂进当前会话 后面 ncm 才叫得出来
$env:PATH = "$ToolsDir;$env:PATH"

Write-Host "==> installing project template"

#站到临时目录再装 当前目录同名的目录会被优先当成模板路径 那样装进去的是本地源码不是 nuget 包
Push-Location $env:TEMP
try {
    Invoke-Dotnet @("new", "install", $TemplateId, "--force")
}
finally {
    Pop-Location
}

Write-Host "==> installing ncm"

$globalTools = Invoke-Dotnet @("tool", "list", "-g")
if ($globalTools -match $ToolId) {
    Invoke-Dotnet @("tool", "update", "-g", $ToolId)
}
else {
    Invoke-Dotnet @("tool", "install", "-g", $ToolId)
}

Write-Host "==> verifying"

if (Test-Command "ncm") {
    Write-Host "ncm ok -> $((Get-Command ncm).Source)"
}
else {
    Write-Host "ncm not on PATH in this session, open a new shell"
}

Write-Host ""
Write-Host "done"
Write-Host ""
Write-Host "if dotnet or ncm is not found in a new shell, add these to PATH:"
Write-Host "  $InstallRoot"
Write-Host "  $ToolsDir"
