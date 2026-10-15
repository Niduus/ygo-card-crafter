#!/usr/bin/env bash
# Builds the engine harness against a ygopro-core checkout. Needs g++, Lua 5.4 and SQLite headers.
# Usage: tools/engine-harness/build.sh <ygopro-core dir> [output path]
set -euo pipefail
core="$(cd "$1" && pwd)"
out="${2:-$(dirname "$0")/harness}"
here="$(cd "$(dirname "$0")" && pwd)"
lua_cflags="$(pkg-config --cflags lua5.4 2>/dev/null || echo -I/usr/include/lua5.4)"
lua_libs="$(pkg-config --libs lua5.4 2>/dev/null || echo -llua5.4)"
# The engine includes <lua.h> as C++; force the extern "C" wrapper so it links with a C-built Lua.
g++ -std=c++17 -O1 -w $lua_cflags -include lua.hpp -I"$core" "$here/harness.cpp" "$core"/*.cpp $lua_libs -lsqlite3 -o "$out"
echo "built $out"
