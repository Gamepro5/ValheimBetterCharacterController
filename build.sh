#!/bin/bash
# Build BetterCharacterController.dll into the repo root.
#
# References the game's own assemblies, so nothing needs copying out of a Windows install.
# Any Valheim install works - client or dedicated server, since the types used are in
# assembly_valheim.dll either way.
#
#   ./build.sh
#   GAME_MANAGED=/path/to/valheim_Data/Managed BEPINEX_CORE=/path/to/BepInEx/core ./build.sh
#   DOTNET=/path/to/dotnet ./build.sh
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"

# Search likely locations rather than assuming one layout, so the build works from wherever
# the repo is cloned.
find_managed() {
    [ -n "${GAME_MANAGED:-}" ] && { echo "$GAME_MANAGED"; return; }
    local c
    for c in \
        "$HERE/../ValheimServer/server/valheim_server_Data/Managed" \
        "$HOME/Desktop/ValheimServer/server/valheim_server_Data/Managed" \
        "$HOME/.local/share/Steam/steamapps/common/Valheim/valheim_Data/Managed" \
        "$HOME/.steam/steam/steamapps/common/Valheim/valheim_Data/Managed" \
        "$HOME/.local/share/Steam/steamapps/common/Valheim dedicated server/valheim_server_Data/Managed"
    do
        [ -f "$c/assembly_valheim.dll" ] && { echo "$c"; return; }
    done
}

find_bepinex() {
    [ -n "${BEPINEX_CORE:-}" ] && { echo "$BEPINEX_CORE"; return; }
    local c
    for c in \
        "$HERE/../ValheimServer/server/BepInEx/core" \
        "$HOME/Desktop/ValheimServer/server/BepInEx/core" \
        "$HOME/.local/share/Steam/steamapps/common/Valheim/BepInEx/core"
    do
        [ -f "$c/BepInEx.dll" ] && { echo "$c"; return; }
    done
}

MANAGED="$(find_managed)"
CORE="$(find_bepinex)"
DOTNET="${DOTNET:-dotnet}"

if [ -z "$MANAGED" ]; then
    echo "Could not find assembly_valheim.dll. Set GAME_MANAGED to a Valheim ..._Data/Managed folder."
    exit 1
fi
if [ -z "$CORE" ]; then
    echo "Could not find BepInEx.dll. Set BEPINEX_CORE to a BepInEx/core folder."
    exit 1
fi
if ! command -v "$DOTNET" >/dev/null 2>&1; then
    echo "dotnet not found. Install the .NET SDK, or set DOTNET=/path/to/dotnet:"
    echo "  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir ~/dotnet --no-path"
    exit 1
fi

echo "game assemblies : $MANAGED"
echo "bepinex core    : $CORE"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
"$DOTNET" build -c Release "$HERE/src/BetterCharacterController.csproj" \
  -p:GameManaged="$MANAGED" \
  -p:BepInExCore="$CORE"

cp "$HERE/src/bin/Release/BetterCharacterController.dll" "$HERE/BetterCharacterController.dll"

echo
echo "Built: $HERE/BetterCharacterController.dll"
sha256sum "$HERE/BetterCharacterController.dll"
wc -c < "$HERE/BetterCharacterController.dll" | sed 's/^/bytes: /'
