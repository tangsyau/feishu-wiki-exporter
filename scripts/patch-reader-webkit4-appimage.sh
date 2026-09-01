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
  x86_64 | aarch64) ;;
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

command -v mksquashfs >/dev/null || fail "mksquashfs is not installed."
mksquashfs_help="$(mksquashfs -help 2>&1 || true)"
[[ -n "$mksquashfs_help" ]] || fail "mksquashfs did not report its supported compressors."
grep -Eiq '(^|[[:space:]])xz([[:space:]]|$)' <<< "$mksquashfs_help" || \
  fail "The system mksquashfs does not support XZ compression."

squashfs_file="$work_dir/filesystem.squashfs"
mksquashfs "$app_dir" "$squashfs_file" \
  -noappend -comp xz -b 131072 -no-progress
[[ -s "$squashfs_file" ]] || fail "mksquashfs did not create an XZ filesystem."

# A type-2 AppImage is its ELF runtime followed by the SquashFS filesystem at
# the offset reported by --appimage-offset. Reuse the original runtime exactly
# so the patched package keeps the same old-distribution compatibility.
patched_appimage="$work_dir/patched.AppImage"
cp "$runtime_file" "$patched_appimage"
cat "$squashfs_file" >> "$patched_appimage"
[[ -s "$patched_appimage" ]] || fail "Could not assemble the patched AppImage."

chmod +x "$patched_appimage"
patched_offset="$("$patched_appimage" --appimage-offset)"
[[ "$patched_offset" == "$runtime_offset" ]] || \
  fail "Patched AppImage offset is '$patched_offset', expected '$runtime_offset'."

validation_dir="$work_dir/validation"
mkdir -p "$validation_dir"
(
  cd "$validation_dir"
  "$patched_appimage" --appimage-extract >/dev/null
) || fail "The patched AppImage runtime could not extract its XZ filesystem."
[[ -x "$validation_dir/squashfs-root/AppRun" ]] || \
  fail "The patched AppImage did not extract its compatibility launcher."
[[ -e "$validation_dir/squashfs-root/AppRun.tauri" ]] || \
  fail "The patched AppImage did not preserve the original Tauri launcher."

mv "$patched_appimage" "$appimage_path"
echo "Patched AppImage written successfully."
