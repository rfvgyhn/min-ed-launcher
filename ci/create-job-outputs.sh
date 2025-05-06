#!/bin/bash
set -e

function log() {
    echo "$1" >&2
}

job_name=Publish
step_name="Create Checksums"
api_url="/repos/$GITHUB_REPOSITORY/actions/runs/$GITHUB_RUN_ID/attempts/$GITHUB_RUN_ATTEMPT/jobs"
log "$api_url"

job=$(gh api "$api_url" --jq ".jobs[] | select(.name==\"$job_name\")")
[[ -z "$job" ]] && { log "Job '$job_name' not found"; exit 1; }

job_id=$(echo "$job" | jq -r .id)
run_url=$(echo "$job" | jq -r .html_url)
checksum_number=$(echo "$job" | jq ".steps[] | select(.name==\"$step_name\") | .number")
[[ -z "$checksum_number" ]] && { log "Step '$step_name'.number not found"; exit 1; }

echo "checksum_url=$run_url#step:$checksum_number:1"
echo "job_id=$job_id"