#!/usr/bin/env bash
# Builds the static WebAssembly site into publish/site, the folder wrangler.jsonc deploys.
# The editor is served under /formawire/ (indaloop.ai/formawire), so its files go into
# publish/site/formawire and its base address is rewritten to match.
# Cloudflare's build image has no .NET, so the SDK is installed into .dotnet when it is missing.
set -euo pipefail
cd "$(dirname "$0")"

# Path the editor is served under. Keep it in step with "routes" in wrangler.jsonc.
SITE_PATH=formawire

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
if ! command -v dotnet >/dev/null 2>&1; then
    curl -sSfL https://dot.net/v1/dotnet-install.sh -o .dotnet-install.sh
    bash .dotnet-install.sh --channel 8.0 --install-dir .dotnet
    export PATH="$PWD/.dotnet:$PATH" DOTNET_ROOT="$PWD/.dotnet"
    # The SDK needs ICU unless told otherwise; the build itself does not use it.
    if ! ldconfig -p 2>/dev/null | grep -q libicu; then export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1; fi
fi

rm -rf publish/wasm publish/site
dotnet publish src/FormaWire.Wasm -c Release -o publish/wasm

mkdir -p publish/site
cp -r publish/wasm/wwwroot "publish/site/$SITE_PATH"
index="publish/site/$SITE_PATH/index.html"
sed -i "s|<base href=\"/\" />|<base href=\"/$SITE_PATH/\" />|" "$index"
grep -q "<base href=\"/$SITE_PATH/\" />" "$index" || { echo "cloudflare-build.sh: <base href=\"/\" /> not found in index.html" >&2; exit 1; }
# Unknown addresses under /formawire/ get the editor, whose router shows the 404 page.
cp "$index" "publish/site/$SITE_PATH/404.html"
# The bare workers.dev address has nothing at its root: send it to the editor.
printf '/ /%s/ 302\n' "$SITE_PATH" > publish/site/_redirects
