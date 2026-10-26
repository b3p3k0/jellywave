#!/usr/bin/env bash
# Shared helpers for the build scripts. Source it: . "$(dirname "$0")/lib.sh"
# ROOT is the repo root.

ROOT="${ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"

# yaml_get KEY: value of a single-line `key: "value"` entry in build.yaml.
# Matches on the key prefix only, so values may contain ': ' themselves.
yaml_get() {
    awk -v k="$1" '
        index($0, k ": ") == 1 {
            sub("^" k ": *", "")
            sub(/^"/, ""); sub(/"$/, "")
            print; exit
        }' "$ROOT/build.yaml"
}

# props_version: <Version> from Directory.Build.props.
props_version() {
    sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props"
}

# require NAME VALUE: abort with a clear message when VALUE is empty.
require() {
    [ -n "$2" ] || { echo "ERROR: $1 is empty (check build.yaml / Directory.Build.props)" >&2; exit 1; }
}
