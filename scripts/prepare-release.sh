#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "Usage: $0 DOWNLOADED_ARTIFACTS_DIR RELEASE_ASSETS_DIR" >&2
  exit 2
fi

project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="$(tr -d '\r\n' < "$project_root/VERSION")"
input_dir="$(cd "$1" && pwd)"
mkdir -p "$2"
output_dir="$(cd "$2" && pwd)"

if find "$output_dir" -mindepth 1 -print -quit | grep -q .; then
  echo "Release assets directory must be empty: $output_dir" >&2
  exit 1
fi

copy_single_file() {
  local artifact_name="$1"
  local pattern="$2"
  local output_name="$3"
  local artifact_dir="$input_dir/$artifact_name"
  local -a matches=()

  if [[ ! -d "$artifact_dir" ]]; then
    echo "Missing artifact directory: $artifact_name" >&2
    exit 1
  fi

  mapfile -d '' matches < <(find "$artifact_dir" -type f -name "$pattern" -print0)
  if [[ ${#matches[@]} -ne 1 ]]; then
    echo "Expected one $pattern file in $artifact_name, found ${#matches[@]}." >&2
    exit 1
  fi

  cp "${matches[0]}" "$output_dir/$output_name"
}

copy_single_file "feishu-wiki-exporter-$version-win-x64" \
  "*.zip" "feishu-wiki-exporter-$version-win-x64.zip"
copy_single_file "feishu-wiki-exporter-$version-win-arm64" \
  "*.zip" "feishu-wiki-exporter-$version-win-arm64.zip"

for arch in x64 arm64; do
  copy_single_file "feishu-wiki-exporter-$version-linux-$arch-appimage" \
    "*.AppImage" "feishu-wiki-exporter-$version-linux-$arch.AppImage"
  copy_single_file "feishu-wiki-exporter-$version-linux-$arch-portable" \
    "*.tar.gz" "feishu-wiki-exporter-$version-linux-$arch-portable.tar.gz"
  copy_single_file "feishu-wiki-exporter-$version-linux-musl-$arch-portable" \
    "*.tar.gz" "feishu-wiki-exporter-$version-linux-musl-$arch-portable.tar.gz"
done

reader_windows_artifact="feishu-wiki-reader-$version-windows-x64-portable"
reader_windows_dir="$input_dir/$reader_windows_artifact"
if [[ ! -d "$reader_windows_dir" ]]; then
  echo "Missing artifact directory: $reader_windows_artifact" >&2
  exit 1
fi

for required_file in FeishuWikiReader.exe LICENSE NOTICE NotoSansSC-OFL-1.1.txt README.txt; do
  if [[ ! -f "$reader_windows_dir/$required_file" ]]; then
    echo "Windows Reader artifact is missing $required_file." >&2
    exit 1
  fi
done

(
  cd "$reader_windows_dir"
  zip -q -r "$output_dir/feishu-wiki-reader-$version-windows-x64-portable.zip" .
)

for arch in x64 arm64; do
  copy_single_file "feishu-wiki-reader-$version-linux-$arch-webkitgtk4.1-appimage" \
    "*.AppImage" "feishu-wiki-reader-$version-linux-$arch-webkitgtk4.1.AppImage"
  copy_single_file "feishu-wiki-reader-$version-linux-$arch-webkitgtk4.1-deb" \
    "*.deb" "feishu-wiki-reader-$version-linux-$arch-webkitgtk4.1.deb"
  copy_single_file "feishu-wiki-reader-$version-linux-$arch-webkitgtk4.1-rpm" \
    "*.rpm" "feishu-wiki-reader-$version-linux-$arch-webkitgtk4.1.rpm"
  copy_single_file "feishu-wiki-reader-$version-linux-$arch-webkitgtk4.0-appimage" \
    "*.AppImage" "feishu-wiki-reader-$version-linux-$arch-webkitgtk4.0.AppImage"
done

package_count="$(find "$output_dir" -maxdepth 1 -type f | wc -l | tr -d ' ')"
if [[ "$package_count" != "17" ]]; then
  echo "Expected 17 release packages, found $package_count." >&2
  exit 1
fi

(
  cd "$output_dir"
  sha256sum ./* | sort -k 2 > SHA256SUMS.txt
)

echo "Prepared 17 packages and SHA256SUMS.txt for version $version."
