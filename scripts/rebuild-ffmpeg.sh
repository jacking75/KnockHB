#!/usr/bin/env bash
set -euo pipefail
# Run only in an isolated Linux build machine with Docker; never in the support UI window.
bundle=${1:?prepared source bundle directory required}
output=${2:?empty output directory required}
[[ ! -e "$output" ]] || { echo 'Output already exists' >&2; exit 1; }
mkdir -p "$output"
tar -xzf "$bundle/build-recipes.tar.gz" -C "$output"
recipes="$output/FFmpeg-Builds-9acad4a9ef1583096af7836cc1e9c8cbcb4d3950"
cd "$recipes"
export GIT_BRANCH_OVERRIDE=330caae0c1acccd2222edc52a05940c574561ce5
export FFMPEG_REPO_OVERRIDE="https://github.com/FFmpeg/FFmpeg.git"
# Recipes pin their dependencies; the supplier build may differ in toolchain/image timestamps.
./makeimage.sh win64 lgpl-shared 8.1
./build.sh win64 lgpl-shared 8.1
docker version --format '{{json .}}' > "$output/toolchain.json"
docker image ls --digests --no-trunc > "$output/images.txt"
cp -a artifacts "$output/archives"
echo 'Rebuild prepared; compare output ABI/configuration/hashes and retain source/toolchain evidence in V02.'
