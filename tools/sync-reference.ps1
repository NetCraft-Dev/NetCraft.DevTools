# 从一次内核构建的输出刷新 reference/ 里的引用程序集
# 内核接口变动之后跑一次 让项目模板发出去的 libs 跟上
# 用法: ./tools/sync-reference.ps1 -KernelOutput <NetCraft.ServerExe 的 bin/Release/net10.0> -ModApiDll <NetCraft.ModApi.dll>
param(
    [Parameter(Mandatory = $true)]
    [string]$KernelOutput,

    [Parameter(Mandatory = $true)]
    [string]$ModApiDll
)

$ErrorActionPreference = "Stop"

$target = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\reference"))
New-Item -ItemType Directory -Path $target -Force | Out-Null

# kernel/ 下是内核子库 主库在输出根目录 ModApi 由 NetCraft.ModApi 仓库单独构建
$sources = @()
$sources += Get-ChildItem (Join-Path $KernelOutput "kernel") -Filter *.dll -ErrorAction Stop
$sources += Get-Item (Join-Path $KernelOutput "NetCraft.dll") -ErrorAction Stop
$sources += Get-Item $ModApiDll -ErrorAction Stop

foreach ($source in $sources) {
    Copy-Item $source.FullName $target -Force
}

Write-Host "synced $($sources.Count) reference assemblies to $target"
