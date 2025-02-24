#!/bin/bash
set -e

root=$(dirname "$(readlink -f "$0")")/..
release_notes="$root"/release-notes.md
artifact_name=$(find "$root/artifacts" -type f -name 'min*' -printf "%f\n" -quit)

"$root"/ci/latest-changes.sh > "$release_notes"
cat << EOF >> "$release_notes"
-----
Verify the release artifacts are built from source by Github by either:
  1. Using the [Github CLI] to verify the integrity and provenance using its associated cryptographically [signed attestations]
  
     \`gh attestation verify $artifact_name -R rfvgyhn/min-ed-launcher\`
  2. Comparing the _shasum.txt_ contents with the _Create Checksums_ section of the job log of the [automated release]
  
     See [wiki] for instructions on how to check the checksums of the release artifacts.

[automated release]: ${BUILD_URL:-https://github.com/rfvgyhn/min-ed-launcher/actions}
[wiki]: https://github.com/rfvgyhn/min-ed-launcher/wiki/Verify-Checksums-for-a-Release
[Github CLI]: https://cli.github.com/
[signed attestations]: https://docs.github.com/en/actions/security-for-github-actions/using-artifact-attestations/using-artifact-attestations-to-establish-provenance-for-builds
EOF