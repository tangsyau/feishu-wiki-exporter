#!/usr/bin/env bash
set -euo pipefail

project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_root="$project_root/artifacts"
version="$(tr -d '\r\n' < "$project_root/VERSION")"
rids=(win-x64 win-arm64 linux-x64 linux-arm64 linux-musl-x64 linux-musl-arm64)

for rid in "${rids[@]}"; do
  publish_dir="$output_root/feishu-wiki-exporter-$version-$rid"
  rm -rf -- "$publish_dir"
  dotnet publish "$project_root/src/FeishuExporter.Desktop/FeishuExporter.Desktop.csproj" \
    -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=false \
    -p:PublishTrimmed=false -o "$publish_dir"

  cp "$project_root/LICENSE" "$project_root/NOTICE" "$project_root/README.md" "$publish_dir/"
  cp "$project_root/src/FeishuExporter.Desktop/Assets/Fonts/OFL-1.1.txt" \
    "$publish_dir/NotoSansSC-OFL-1.1.txt"
done

echo "Published to $output_root"
