#!/bin/bash
set -e

file_name=shasum.txt

pushd $(dirname "$(readlink -f "$0")")/../artifacts > /dev/null
find . -type f ! -name $file_name -printf '%P\n' | xargs shasum -a 256 -b > $file_name
cat $file_name
popd > /dev/null