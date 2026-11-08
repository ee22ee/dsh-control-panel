#!/usr/bin/env bash
# 构建 DshPanel2。
#
# 覆盖运行中的 exe 会失败（Windows 允许改名不允许覆盖运行中的 exe，坑 P30），
# 所以先停进程再编译。
#
# 用法:
#   ./build.sh            编译 Debug
#   ./build.sh release    发布（framework-dependent，单目录）
#   ./build.sh selftest   编译并跑内置自检

set -e
cd "$(dirname "$0")"

MODE="${1:-debug}"

echo "== 停止正在运行的面板 =="
taskkill //IM DshPanel2.exe //F >/dev/null 2>&1 || true
sleep 0.5

case "$MODE" in
  release)
    echo "== 发布 (Release) =="
    dotnet publish -c Release -r win-x64 --self-contained false \
      -p:PublishSingleFile=false -o dist --nologo -v minimal
    echo "输出目录: $(pwd)/dist"
    ls -la dist/DshPanel2.exe 2>/dev/null || true
    ;;

  selftest)
    echo "== 编译 (Debug) =="
    dotnet build -c Debug --nologo -v quiet
    BIN="$(pwd)/bin/Debug/net10.0-windows"
    echo "== 运行内置自检 =="
    set +e
    "$BIN/DshPanel2.exe" --selftest
    CODE=$?
    set -e
    echo
    cat "$BIN/selftest.log" 2>/dev/null || true
    echo
    echo "自检退出码: $CODE"
    exit $CODE
    ;;

  *)
    echo "== 编译 (Debug) =="
    dotnet build -c Debug --nologo -v quiet
    echo "输出: $(pwd)/bin/Debug/net10.0-windows/DshPanel2.exe"
    ;;
esac
