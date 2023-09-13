#!/bin/bash
set -e

root=$(dirname "$(readlink -f "$0")")/..
release_notes="$root"/release-notes.md

"$root"/ci/latest-changes.sh > "$release_notes"
echo -n "-----
Verify the release artifacts are built from source by Github by comparing the _shasum.txt_ contents with the the _Create Checksums_ section of the job log of the [automated release].

See [wiki] for instructions on how to check the checksums of the release artifacts.

[automated release]: ${BUILD_URL:-https://github.com/rfvgyhn/min-ed-launcher/actions}
[wiki]: https://github.com/rfvgyhn/min-ed-launcher/wiki/Verify-Checksums-for-a-Release" >> "$release_notes"
