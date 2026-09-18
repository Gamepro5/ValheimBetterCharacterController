#!/bin/bash
# Build BetterCharacterController.dll.
#
# References the game's own assemblies, so no DLLs need copying from a Windows install.
# Point GAME_MANAGED and BEPINEX_CORE at any Valheim install (client or dedicated server).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"

GAME_MANAGED="${GAME_MANAGED:-$HERE/../ValheimServer/server/valheim_server_Data/Managed}"
BEPINEX_CORE="${BEPINEX_CORE:-$HERE/../ValheimServer/server/BepInEx/core}"
DOTNET="${DOTNET:-dotnet}"

command -v "$DOTNET" >/dev/null 2>&1 || {
    echo "dotnet not found. Install the .NET SDK, or set DOTNET=/path/to/dotnet"
    echo "  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir ~/dotnet --no-path"
    exit 1
}
[ -f "$GAME_MANAGED/assembly_valheim.dll" ] || { echo "assembly_valheim.dll not under $GAME_MANAGED - set GAME_MANAGED"; exit 1; }
[ -f "$BEPINEX_CORE/BepInEx.dll" ]          || { echo "BepInEx.dll not under $BEPINEX_CORE - set BEPINEX_CORE"; exit 1; }

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
"$DOTNET" build -c Release "$HERE/src/BetterCharacterController.csproj" \
  -p:GameManaged="$GAME_MANAGED" \
  -p:BepInExCore="$BEPINEX_CORE"

cp "$HERE/src/bin/Release/BetterCharacterController.dll" "$HERE/BetterCharacterController.dll"
echo
echo "Built: $HERE/BetterCharacterController.dll"
sha256sum "$HERE/BetterCharacterController.dll"
