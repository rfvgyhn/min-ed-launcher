$ErrorActionPreference = "Stop"

$target="win-x64"
[xml]$proj = Get-Content src\Directory.Build.props
$version=$proj.Project.PropertyGroup.VersionPrefix
$release_name="min-ed-launcher_v${version}_$target"
$target_dir="artifacts\$release_name"

dotnet publish -r "$target" --self-contained -o "$target_dir" -c ReleaseWindows -p:PublishSingleFile=true src\MinEdLauncher\MinEdLauncher.fsproj
$full_version=(Get-Item "$target_dir\MinEdLauncher.exe").VersionInfo.ProductVersion
(Get-Content Cargo.toml).replace('0.0.0', "$full_version") | Set-Content Cargo.toml # Workaround for https://github.com/rust-lang/cargo/issues/6583
cargo build --release
(Get-Content Cargo.toml).replace("$full_version", '0.0.0') | Set-Content Cargo.toml # Workaround for https://github.com/rust-lang/cargo/issues/6583
mv target/release/bootstrap.exe "$target_dir\MinEdLauncher.Bootstrap.exe"
cp README.md,CHANGELOG.md "$target_dir"
rm "$target_dir\*" -include *.json, *.pdb

Compress-Archive -Path "$target_dir" -DestinationPath "$target_dir.zip" -Force

rm -r "$target_dir"
