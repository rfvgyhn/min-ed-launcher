param([string]$tag="dev")
$ErrorActionPreference = "Stop"

$target="win10-x64"
$release_name="min-ed-launcher-$tag-$target"

dotnet publish src\EdLauncher\EdLauncher.fsproj -r "$target" --self-contained true -o "artifacts\$release_name" -c ReleaseWindows
dotnet publish src\EdLauncher.Bootstrap\EdLauncher.Bootstrap.csproj -r "$target" --self-contained true -o "artifacts\$release_name" -c Release
rm "artifacts\$release_name\*" -include *.json, *.pdb

Compress-Archive -Path "artifacts\$release_name" -DestinationPath "artifacts\$release_name.zip" -Force

rm -r "artifacts\$release_name"
