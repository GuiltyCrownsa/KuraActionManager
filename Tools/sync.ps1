# 把 Unity 工程里正在开发的包同步到本仓库，然后提交推送。
# 用法：在仓库根目录执行
#   pwsh -File Tools\sync.ps1 -UnityProject "D:\你的Unity工程"            # 只同步
#   pwsh -File Tools\sync.ps1 -UnityProject "D:\你的Unity工程" -Commit "feat: xxx"
param(
    [Parameter(Mandatory = $true)][string]$UnityProject,
    [string]$Commit
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$name = "com.kura.actionmanager"
$src = Join-Path $UnityProject "Packages\$name"
$dst = Join-Path $repo "Packages\$name"

if (-not (Test-Path $src)) { throw "找不到包目录: $src" }

# 镜像复制：先删掉旧内容，避免已删除的文件残留
if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null
Copy-Item $src $dst -Recurse -Force

Write-Host "已同步到 $dst"

& git -C $repo add -A
if ($Commit) {
    & git -C $repo commit -m $Commit
    & git -C $repo push
} else {
    & git -C $repo status --short
}
