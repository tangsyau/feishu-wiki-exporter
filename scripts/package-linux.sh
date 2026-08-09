#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 4 || $# -gt 5 ]]; then
  echo "Usage: $0 PUBLISH_DIR RID OUTPUT_DIR VERSION [--appimage]" >&2
  exit 2
fi

publish_dir="$(cd "$1" && pwd)"
rid="$2"
mkdir -p "$3"
output_dir="$(cd "$3" && pwd)"
version="$4"
make_appimage="${5:-}"
project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$ ]]; then
  echo "Invalid semantic version: $version" >&2
  exit 2
fi

case "$rid" in
  linux-x64)
    appimage_arch="x86_64"
    ;;
  linux-arm64)
    appimage_arch="aarch64"
    ;;
  linux-musl-x64|linux-musl-arm64)
    appimage_arch=""
    ;;
  *)
    echo "Unsupported Linux RID: $rid" >&2
    exit 2
    ;;
esac

if [[ ! -x "$publish_dir/FeishuWikiExporter" ]]; then
  echo "FeishuWikiExporter is missing or not executable in $publish_dir." >&2
  exit 1
fi

work_dir="$(mktemp -d)"
trap 'rm -rf -- "$work_dir"' EXIT

portable_name="feishu-wiki-exporter-$version-$rid"
portable_dir="$work_dir/$portable_name"
mkdir -p "$portable_dir"
cp -a "$publish_dir/." "$portable_dir/"
cp "$project_root/LICENSE" "$project_root/NOTICE" "$project_root/README.md" "$portable_dir/"
cp "$project_root/src/FeishuExporter.Desktop/Assets/Fonts/OFL-1.1.txt" "$portable_dir/NotoSansSC-OFL-1.1.txt"
tar -C "$work_dir" -czf "$output_dir/$portable_name-portable.tar.gz" "$portable_name"

if [[ "$make_appimage" != "--appimage" ]]; then
  exit 0
fi

if [[ -z "$appimage_arch" ]]; then
  echo "AppImage is only produced for glibc Linux RIDs." >&2
  exit 2
fi

app_dir="$work_dir/FeishuWikiExporter.AppDir"
app_lib_dir="$app_dir/usr/lib/feishu-wiki-exporter"
doc_dir="$app_dir/usr/share/doc/feishu-wiki-exporter"
mkdir -p "$app_lib_dir" "$doc_dir" "$app_dir/usr/bin" "$app_dir/usr/share/applications" "$app_dir/usr/share/metainfo"
cp -a "$publish_dir/." "$app_lib_dir/"
cp "$project_root/LICENSE" "$project_root/NOTICE" "$project_root/README.md" "$doc_dir/"
cp "$project_root/src/FeishuExporter.Desktop/Assets/Fonts/OFL-1.1.txt" "$doc_dir/NotoSansSC-OFL-1.1.txt"
cp "$project_root/packaging/linux/AppRun" "$app_dir/AppRun"
cp "$project_root/packaging/linux/feishu-wiki-exporter.desktop" "$app_dir/"
cp "$project_root/packaging/linux/feishu-wiki-exporter.desktop" "$app_dir/usr/share/applications/"
cp "$project_root/packaging/linux/io.github.tangsyau.feishu-wiki-exporter.metainfo.xml" "$app_dir/usr/share/metainfo/"
cp "$project_root/src/FeishuExporter.Desktop/Assets/AppIcon.png" "$app_dir/feishu-wiki-exporter.png"
ln -s ../lib/feishu-wiki-exporter/FeishuWikiExporter "$app_dir/usr/bin/FeishuWikiExporter"
ln -s feishu-wiki-exporter.png "$app_dir/.DirIcon"
chmod +x "$app_dir/AppRun" "$app_lib_dir/FeishuWikiExporter"

appimagetool="$work_dir/appimagetool-x86_64.AppImage"
appimagetool_url="${APPIMAGETOOL_URL:-https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage}"
curl --fail --location --silent --show-error --retry 3 --retry-delay 2 "$appimagetool_url" --output "$appimagetool"
chmod +x "$appimagetool"

appimage_path="$output_dir/feishu-wiki-exporter-$version-$rid.AppImage"
ARCH="$appimage_arch" APPIMAGE_EXTRACT_AND_RUN=1 "$appimagetool" "$app_dir" "$appimage_path"
test -s "$appimage_path"
chmod +x "$appimage_path"
