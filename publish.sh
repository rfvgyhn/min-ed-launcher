#!/bin/bash

target="linux-x64"
version=$(grep -oPm1 "(?<=<VersionPrefix>)[^<]+" src/Directory.Build.props) # use something like xml_grep if this regex becomes a problem
release_name="min-ed-launcher_v${version}_$target"

dotnet restore -r $target
dotnet publish src/MinEdLauncher/MinEdLauncher.fsproj -r "$target" --self-contained true --no-restore -o "artifacts/$release_name" -c Release -p:PublishSingleFile=true
cp README.md "artifacts/$release_name"
rm artifacts/"$release_name"/*.pdb

tar czvf "artifacts/$release_name.tar.gz" -C "artifacts" "$release_name"

rm -r "artifacts/$release_name"
