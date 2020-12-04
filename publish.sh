#!/bin/bash

target="linux-x64"
tag="${1:-dev}"
release_name="min-ed-launcher-$tag-$target"

dotnet publish src/EdLauncher/EdLauncher.fsproj -r "$target" --self-contained true -o "artifacts/$release_name" -c Release
rm artifacts/"$release_name"/*.pdb

tar czvf "artifacts/$release_name.tar.gz" -C "artifacts" "$release_name"

rm -r "artifacts/$release_name"
