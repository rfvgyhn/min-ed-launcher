#!/bin/bash
set -e

job_id="$1"
raw_log_url=$(curl -Ls -o /dev/null \
    -w %{url_effective} \
    -H "Accept: application/vnd.github+json" \
    -H "Authorization: Bearer $GITHUB_TOKEN" \
    -H "X-GitHub-Api-Version: 2022-11-28" \
    https://api.github.com/repos/${GITHUB_REPOSITORY}/actions/jobs/$job_id/logs
)
archived_url=$(curl -L -s -o /dev/null -w "%header{link}" "http://web.archive.org/save/$raw_log_url" \
    | awk '/^</ {
        split($0, links, ",")
        
        for(i=1; i<=length(links); i++) {
            link = links[i]
            
            gsub(/<|>/, "", link)     # remove angle brackets
            gsub(/^ *| *$/, "", link) # remove leading/trailing whitespace
            
            if(link ~ "rel=\"memento\"") {
                split(link, parts, ";")
                gsub(/^http:/, "https:", parts[1])
                print parts[1]
            }
        }
    }'
)
echo "archived_url=$archived_url"