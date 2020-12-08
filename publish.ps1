$ErrorActionPreference = "Stop"

$target="win10-x64"
[xml]$proj = Get-Content src\Directory.Build.props
$version=$proj.Project.PropertyGroup.VersionPrefix
$release_name="min-ed-launcher_v${version}_$target"

dotnet publish src\MinEdLauncher\MinEdLauncher.fsproj -r "$target" --self-contained true -o "artifacts\$release_name" -c ReleaseWindows -p:PublishSingleFile=true
dotnet publish src\MinEdLauncher.Bootstrap\MinEdLauncher.Bootstrap.csproj -r "$target" --self-contained true -o "artifacts\$release_name" -c Release
cp README.md "artifacts\$release_name"
rm "artifacts\$release_name\*" -include *.json, *.pdb

Compress-Archive -Path "artifacts\$release_name" -DestinationPath "artifacts\$release_name.zip" -Force

rm -r "artifacts\$release_name"
