#!/usr/bin/env sh
# Builds the plugin folder a Turbo server loads, dist/events/: manifest.json, the C# sources
# (server/**/*.cs) and the plugin's files. The server compiles the sources as it loads
# the plugin: the folder holds nothing built. The sources are compiled first as the server does
# (server/TurboEvents.csproj imports the server's plugin-sdk/Turbo.Plugin.props), so a folder that would
# not load is not made.
# TurboBin: the server's bin folder to build against (default: ../turbo-cloud's built Turbo.Main).
set -e
cd "$(dirname "$0")/.."
dotnet build server/TurboEvents.csproj -nologo -v q -clp:NoSummary
rm -rf dist/events
mkdir -p dist/events
cp manifest.json dist/events/
find server -name '*.cs' ! -path '*/bin/*' ! -path '*/obj/*' | tar -cf - -T - | tar -xf - -C dist/events
for folder in wwwroot gamedata client/src; do
  if [ -d "$folder" ]; then mkdir -p "dist/events/$(dirname "$folder")" && cp -r "$folder" "dist/events/$folder"; fi
done
echo "dist/events: $(find dist/events -type f | wc -l) files"
