#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/src/Archie.Scanner.DotNet/Archie.Scanner.DotNet.csproj"
manifest="$repo_root/src/Archie.Scanner.DotNet/scanner.json"
version="${1:-$(jq -r .version "$manifest")}"
release_root="$repo_root/artifacts"
stage="$release_root/stage"
archive="$release_root/entorn-scanner-dotnet-linux-x64-$version.tar.gz"
package="$stage/PACKAGE.json"

[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$ ]] || { echo "Invalid semantic version: $version" >&2; exit 2; }
[[ "$(jq -r .id "$manifest")" == "archie.dotnet" ]] || { echo "Unexpected scanner identity." >&2; exit 2; }
[[ "$(jq -r .version "$manifest")" == "$version" ]] || { echo "Tag/package version must match scanner.json." >&2; exit 2; }

rm -rf "$stage" "$archive" "$archive.sha256"
mkdir -p "$stage"
dotnet restore "$project" --locked-mode --runtime linux-x64
dotnet publish "$project" --configuration Release --runtime linux-x64 --self-contained false --no-restore \
  -p:UseAppHost=false --output "$stage"
cp "$repo_root/LICENSE" "$stage/LICENSE"
cp "$repo_root/THIRD-PARTY-NOTICES.txt" "$stage/THIRD-PARTY-NOTICES.txt"
touch "$package"

entry_count="$(find "$stage" -type f | wc -l | tr -d ' ')"
expanded_bytes=0
for _ in {1..10}; do
  jq -n \
    --arg id "$(jq -r .id "$manifest")" \
    --arg version "$version" \
    --argjson expandedBytes "$expanded_bytes" \
    --argjson entryCount "$entry_count" \
    --argjson capabilities "$(jq -c .capabilities "$manifest")" \
    --argjson permissions "$(jq -c .permissions "$manifest")" \
    '{schemaVersion:"scanner-package/v1",id:$id,version:$version,sourceRepository:"https://github.com/Entorn-dev/entorn-scanner-dotnet",sourceTag:("v"+$version),platform:"linux",architecture:"x64",archieVersionRange:"[1.0.0,2.0.0)",protocolVersion:"scanner/v1",expandedBytes:$expandedBytes,entryCount:$entryCount,capabilities:$capabilities,permissions:$permissions,license:"Apache-2.0",publisherKeyId:"entorn-scanner-signing-2026-01"}' > "$package"
  actual="$(find "$stage" -type f -printf '%s\n' | awk '{sum += $1} END {print sum + 0}')"
  [[ "$actual" == "$expanded_bytes" ]] && break
  expanded_bytes="$actual"
done
[[ "$(jq -r .expandedBytes "$package")" == "$(find "$stage" -type f -printf '%s\n' | awk '{sum += $1} END {print sum + 0}')" ]] || { echo "PACKAGE.json size did not stabilize." >&2; exit 1; }

find "$stage" -type d -exec chmod 755 {} +
find "$stage" -type f -exec chmod 644 {} +
find "$stage" -exec touch -h -d '@0' {} +
file_list="$(mktemp)"
trap 'rm -f "$file_list"' EXIT
find "$stage" -type f -printf '%P\n' | LC_ALL=C sort > "$file_list"
tar --format=ustar --owner=0 --group=0 --numeric-owner --mtime='@0' \
  --no-recursion -C "$stage" -cf - -T "$file_list" | gzip -n -9 > "$archive"
(cd "$release_root" && sha256sum "$(basename "$archive")" > "$(basename "$archive").sha256")
printf '%s\n' "$archive"
