#!/usr/bin/env bash
# Builds the static WebAssembly site into publish/wasm/wwwroot, the folder wrangler.jsonc deploys.
# Cloudflare's build image has no .NET, so the SDK is installed into .dotnet when it is missing.
set -euo pipefail
cd "$(dirname "$0")"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
if ! command -v dotnet >/dev/null 2>&1; then
    curl -sSfL https://dot.net/v1/dotnet-install.sh -o .dotnet-install.sh
    bash .dotnet-install.sh --channel 8.0 --install-dir .dotnet
    export PATH="$PWD/.dotnet:$PATH" DOTNET_ROOT="$PWD/.dotnet"
    # The SDK needs ICU unless told otherwise; the build itself does not use it.
    if ! ldconfig -p 2>/dev/null | grep -q libicu; then export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1; fi
fi

rm -rf publish/wasm
dotnet publish src/FormaWire.Wasm -c Release -o publish/wasm
