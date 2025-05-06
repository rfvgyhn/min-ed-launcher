#!/bin/bash

job_name=Publish
step_name="Create Checksums"
job=$(curl -Ls \
    -H "Accept: application/vnd.github+json" \
    -H "Authorization: Bearer $GITHUB_TOKEN"\
    -H "X-GitHub-Api-Version: 2022-11-28" \
    https://api.github.com/repos/$GITHUB_REPOSITORY/actions/runs/$GITHUB_RUN_ID/attempts/$GITHUB_RUN_ATTEMPT/jobs \
    | jq -r ".jobs[] | select(.name==\"$job_name\")"
)
run_url=$(echo "$job" | jq -r .html_url)
checksum_number=$(echo "$job" | jq ".steps[] | select(.name==\"$step_name\") | .number")

echo "checksum_url=$run_url#step:$checksum_number:1"
echo "job_id=$GITHUB_JOB"