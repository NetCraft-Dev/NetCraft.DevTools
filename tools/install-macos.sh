#!/usr/bin/env bash
# NetCraft 开发工具链一键安装（macOS）
# 装三样 .NET 10 SDK dotnet new 项目模板 ncm 命令行工具
# 幂等 已经有的会跳过或就地更新 重复跑不会出问题
# 用法 ./tools/install-macos.sh

set -eu

CHANNEL="10.0"
TEMPLATE_ID="NetCraft.ModsProjectType"
TOOL_ID="NetCraft.ModBuild.Tools"
INSTALL_ROOT="$HOME/.dotnet"
TOOLS_DIR="$INSTALL_ROOT/tools"
SYSTEM_ROOT="/usr/local/share/dotnet"

#TestCommand 命令在不在 PATH 上
TestCommand() {
    command -v "$1" >/dev/null 2>&1
}

#TestSdk 本机有没有指定大版本的 SDK
TestSdk() {
    TestCommand dotnet || return 1
    dotnet --list-sdks 2>/dev/null | grep -q "^$1\."
}

#Download 有 curl 用 curl 没有就退到 wget
Download() {
    if TestCommand curl; then
        curl -fsSL "$1" -o "$2"
    elif TestCommand wget; then
        wget -qO "$2" "$1"
    else
        echo "need curl or wget to download $1" >&2
        exit 1
    fi
}

#UseHomebrew 有 brew 就让 brew 去装 装到系统目录 不用再管 PATH
UseHomebrew() {
    TestCommand brew || return 1
    echo "installing dotnet-sdk with Homebrew"
    brew install --cask dotnet-sdk
    hash -r
    TestSdk 10
}

#InstallSdk 官方脚本把 SDK 装进用户目录 没装 brew 时走这条
InstallSdk() {
    script="$(mktemp)"
    echo "downloading dotnet-install.sh"
    Download "https://dot.net/v1/dotnet-install.sh" "$script"
    echo "installing .NET SDK $CHANNEL into $INSTALL_ROOT"
    bash "$script" --channel "$CHANNEL" --install-dir "$INSTALL_ROOT" --no-path
    rm -f "$script"
    PATH="$INSTALL_ROOT:$PATH"
    export PATH
}

echo "==> checking .NET SDK"

if TestSdk 10; then
    echo "found SDK $(dotnet --version)"
elif UseHomebrew; then
    echo "installed SDK $(dotnet --version) into $SYSTEM_ROOT"
else
    InstallSdk
    TestSdk 10 || { echo ".NET 10 SDK still missing after install" >&2; exit 1; }
    echo "installed SDK $(dotnet --version)"
fi

#工具目录也挂进当前会话 后面 ncm 才叫得出来
PATH="$TOOLS_DIR:$PATH"
export PATH

echo "==> installing project template"

dotnet new install "$TEMPLATE_ID"

echo "==> installing ncm"

if dotnet tool list -g 2>/dev/null | grep -qi "$TOOL_ID"; then
    dotnet tool update -g "$TOOL_ID"
else
    dotnet tool install -g "$TOOL_ID"
fi

echo "==> verifying"

if TestCommand ncm; then
    echo "ncm ok -> $(command -v ncm)"
else
    echo "ncm not on PATH in this session, open a new shell"
fi

echo ""
echo "done"
echo ""
echo "if ncm is not found in a new shell, add this to your shell profile:"
echo "  export PATH=\"$TOOLS_DIR:\$PATH\""
