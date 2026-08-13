#!/usr/bin/env bash

set -euo pipefail

expected_machine="${1:?Usage: verify-reader-webkit4.sh <machine> <deb-architecture> [release-directory]}"
expected_deb_arch="${2:?Usage: verify-reader-webkit4.sh <machine> <deb-architecture> [release-directory]}"
release_dir="${3:-knowledge-reader/legacy-tauri-v1/src-tauri/target/release}"

fail() {
  echo "::error::$*" >&2
  exit 1
}

echo "== WebKitGTK 4.0 experimental package verification =="
actual_machine="$(uname -m)"
echo "Host architecture: ${actual_machine} (expected: ${expected_machine})"
[[ "$actual_machine" == "$expected_machine" ]] || \
  fail "Runner architecture is '${actual_machine}', expected '${expected_machine}'."

[[ -d "$release_dir" ]] || fail "Release directory does not exist: ${release_dir}"

echo "Release files:"
find "$release_dir" -maxdepth 4 -type f -print | sort

binary="$(find "$release_dir" -maxdepth 1 -type f -name 'feishu-wiki-reader-webkitgtk4' -print -quit)"
deb_file="$(find "$release_dir/bundle/deb" -maxdepth 1 -type f -name '*.deb' -print -quit 2>/dev/null || true)"
appimage_file="$(find "$release_dir/bundle/appimage" -maxdepth 1 -type f -name '*.AppImage' -print -quit 2>/dev/null || true)"

[[ -n "$binary" ]] || fail "Reader executable was not found directly under ${release_dir}."
[[ -x "$binary" ]] || fail "Reader executable is not executable: ${binary}"
[[ -n "$deb_file" ]] || fail "No DEB package was found under ${release_dir}/bundle/deb."
[[ -n "$appimage_file" ]] || fail "No AppImage was found under ${release_dir}/bundle/appimage."

echo "Reader executable: ${binary}"
echo "DEB package:       ${deb_file}"
echo "AppImage package:  ${appimage_file}"
file "$binary" "$deb_file" "$appimage_file"

echo "Dynamic dependencies reported by ldd:"
if ! dynamic_dependencies="$(ldd "$binary" 2>&1)"; then
  echo "$dynamic_dependencies"
  fail "ldd could not inspect the Reader executable."
fi
echo "$dynamic_dependencies"

grep -Fq 'libwebkit2gtk-4.0.so.37' <<< "$dynamic_dependencies" || \
  fail "Reader executable does not resolve libwebkit2gtk-4.0.so.37."
if grep -Fq 'libwebkit2gtk-4.1' <<< "$dynamic_dependencies"; then
  fail "Experimental Reader unexpectedly resolves a WebKitGTK 4.1 library."
fi

echo "DEB package metadata:"
dpkg-deb --info "$deb_file"
actual_deb_arch="$(dpkg-deb --field "$deb_file" Architecture)"
deb_dependencies="$(dpkg-deb --field "$deb_file" Depends)"
echo "DEB architecture: ${actual_deb_arch} (expected: ${expected_deb_arch})"
echo "DEB dependencies: ${deb_dependencies}"
[[ "$actual_deb_arch" == "$expected_deb_arch" ]] || \
  fail "DEB architecture is '${actual_deb_arch}', expected '${expected_deb_arch}'."
grep -Fq 'libwebkit2gtk-4.0-37' <<< "$deb_dependencies" || \
  fail "DEB does not declare a dependency on libwebkit2gtk-4.0-37."
if grep -Fq 'libwebkit2gtk-4.1' <<< "$deb_dependencies"; then
  fail "Experimental DEB unexpectedly declares a WebKitGTK 4.1 dependency."
fi

max_glibc="$({ objdump -T "$binary" 2>/dev/null || true; } | \
  sed -n 's/.*\(GLIBC_[0-9][0-9.]*\).*/\1/p' | sort -Vu | tail -1)"
[[ -n "$max_glibc" ]] || fail "No GLIBC version symbols were found in the Reader executable."
newest_glibc="$(printf '%s\n' GLIBC_2.28 "$max_glibc" | sort -V | tail -1)"
echo "Maximum required glibc symbol: ${max_glibc}"
[[ "$newest_glibc" == 'GLIBC_2.28' ]] || \
  fail "Reader requires ${max_glibc}, which is newer than the GLIBC_2.28 compatibility target."

echo "Verification completed successfully."
