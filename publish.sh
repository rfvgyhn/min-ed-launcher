#!/bin/bash

target="linux-x64"
version=$(grep -oPm1 "(?<=<Version>)[^<]+" src/MinEdLauncher/MinEdLauncher.fsproj) # use something like xml_grep if this regex becomes a problem
release_name="min-ed-launcher-$version-$target"

dotnet publish src/MinEdLauncher/MinEdLauncher.fsproj -r "$target" --self-contained true -o "artifacts/$release_name" -c Release
rm artifacts/"$release_name"/*.pdb

tar czvf "artifacts/$release_name.tar.gz" -C "artifacts" "$release_name"

rm -r "artifacts/$release_name"
