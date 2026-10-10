# Onta — C# build (GNU Make)
#
# Windows で make が無い場合は PowerShell を使う:
#   .\make.ps1
#   .\make.cmd build
#
# Usage (GNU Make):
#   make
#   make CONFIG=Debug
#   make test
#   make testdebug
#   make clean
#
# DOTNET のパス指定例:
#   make DOTNET="C:/Program Files/dotnet/dotnet.exe"

DOTNET  ?= dotnet
PROJECT := Onta_core/Onta_core.csproj
TEST_PROJECT := Onta_core/test/Onta_core.Tests.csproj
CONFIG  ?= Release

.PHONY: all build clean rebuild test testdebug

all: build

build:
	"$(DOTNET)" build "$(PROJECT)" -c "$(CONFIG)" --nologo
	"$(DOTNET)" build "$(TEST_PROJECT)" -c "$(CONFIG)" --nologo

rebuild: clean build

clean:
	"$(DOTNET)" clean "$(PROJECT)" -c "$(CONFIG)" --nologo
	"$(DOTNET)" clean "$(TEST_PROJECT)" -c "$(CONFIG)" --nologo

test: build
	"$(DOTNET)" test "$(TEST_PROJECT)" -c "$(CONFIG)" --no-build --nologo

# ブレークポイント用（Debug / 最適化なし）
testdebug:
	"$(DOTNET)" build "$(PROJECT)" -c Debug --nologo
	"$(DOTNET)" build "$(TEST_PROJECT)" -c Debug --nologo
	"$(DOTNET)" test "$(TEST_PROJECT)" -c Debug --no-build --nologo
