#!/usr/bin/env bash

set -euo pipefail

appimage_path="${1:?Usage: patch-reader-webkit4-appimage.sh <AppImage> <machine>}"
expected_machine="${2:?Usage: patch-reader-webkit4-appimage.sh <AppImage> <machine>}"

project_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
launcher_template="$project_root/packaging/linux/reader-webkit4/AppRun"

fail() {
  echo "::error::$*" >&2
  exit 1
}

[[ -f "$appimage_path" ]] || fail "AppImage does not exist: $appimage_path"
[[ -x "$appimage_path" ]] || chmod +x "$appimage_path"
[[ -f "$launcher_template" ]] || fail "Compatibility launcher does not exist: $launcher_template"

case "$expected_machine" in
  x86_64) appimage_arch="x86_64" ;;
  aarch64) appimage_arch="aarch64" ;;
  *) fail "Unsupported AppImage architecture: $expected_machine" ;;
esac

actual_machine="$(uname -m)"
[[ "$actual_machine" == "$expected_machine" ]] || \
  fail "Runner architecture is '$actual_machine', expected '$expected_machine'."

appimage_path="$(realpath "$appimage_path")"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

echo "Patching WebKitGTK 4.0 AppImage: $appimage_path"
runtime_offset="$("$appimage_path" --appimage-offset)"
[[ "$runtime_offset" =~ ^[0-9]+$ ]] || \
  fail "AppImage did not report a valid type-2 runtime offset: $runtime_offset"
runtime_file="$work_dir/original-runtime"
head -c "$runtime_offset" "$appimage_path" > "$runtime_file"
[[ -s "$runtime_file" ]] || fail "Could not preserve the original AppImage runtime."
(
  cd "$work_dir"
  "$appimage_path" --appimage-extract >/dev/null
)

app_dir="$work_dir/squashfs-root"
[[ -d "$app_dir" ]] || fail "AppImage extraction did not create squashfs-root."
[[ -e "$app_dir/AppRun" ]] || fail "Extracted AppImage does not contain AppRun."

mv "$app_dir/AppRun" "$app_dir/AppRun.tauri"
install -m 0755 "$launcher_template" "$app_dir/AppRun"

mapfile -t bundled_wayland_libraries < <(
  find "$app_dir" \( -type f -o -type l \) \
    \( -name 'libwayland-client.so*' \
      -o -name 'libwayland-cursor.so*' \
      -o -name 'libwayland-egl.so*' \
      -o -name 'libwayland-server.so*' \) \
    -print | sort
)

if [[ "${#bundled_wayland_libraries[@]}" -gt 0 ]]; then
  echo "Removing bundled low-level Wayland libraries:"
  printf '  %s\n' "${bundled_wayland_libraries[@]#$app_dir/}"
  rm -f -- "${bundled_wayland_libraries[@]}"
else
  echo "No bundled low-level Wayland libraries were found."
fi

appimagetool_path="${APPIMAGETOOL_PATH:-$work_dir/appimagetool-${appimage_arch}.AppImage}"
if [[ -z "${APPIMAGETOOL_PATH:-}" ]]; then
  appimagetool_url="https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-${appimage_arch}.AppImage"
  curl --fail --location --silent --show-error --retry 3 --retry-delay 2 \
    "$appimagetool_url" --output "$appimagetool_path"
  chmod +x "$appimagetool_path"
fi
[[ -x "$appimagetool_path" ]] || fail "appimagetool is not executable: $appimagetool_path"

patched_appimage="$work_dir/patched.AppImage"
ARCH="$appimage_arch" APPIMAGE_EXTRACT_AND_RUN=1 \
  "$appimagetool_path" --runtime-file "$runtime_file" --comp xz \
    "$app_dir" "$patched_appimage"
[[ -s "$patched_appimage" ]] || fail "appimagetool did not create a patched AppImage."

chmod +x "$patched_appimage"
mv "$patched_appimage" "$appimage_path"
echo "Patched AppImage written successfully."
