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

deb_file="$(find "$release_dir/bundle/deb" -maxdepth 1 -type f -name '*.deb' -print -quit 2>/dev/null || true)"
appimage_file="$(find "$release_dir/bundle/appimage" -maxdepth 1 -type f -name '*.AppImage' -print -quit 2>/dev/null || true)"

[[ -n "$deb_file" ]] || fail "No DEB package was found under ${release_dir}/bundle/deb."
[[ -n "$appimage_file" ]] || fail "No AppImage was found under ${release_dir}/bundle/appimage."

echo "Release root files:"
find "$release_dir" -maxdepth 1 -type f -printf '  %f (%s bytes)\n' | sort
echo "Generated packages:"
find "$release_dir/bundle" -maxdepth 2 -type f \
  \( -name '*.deb' -o -name '*.AppImage' \) \
  -printf '  %p (%s bytes)\n' | sort

echo "DEB package:      ${deb_file}"
echo "AppImage package: ${appimage_file}"
file "$deb_file" "$appimage_file"

# Tauri 1 may derive the packaged executable name from productName rather than
# the Cargo package name. Inspect the executable that users will actually
# install instead of assuming a fixed file name in target/release.
inspection_dir="$(mktemp -d)"
trap 'rm -rf "$inspection_dir"' EXIT
dpkg-deb --extract "$deb_file" "$inspection_dir"
mapfile -t packaged_binaries < <(
  find "$inspection_dir/usr/bin" -maxdepth 1 -type f -executable -print 2>/dev/null | sort
)
[[ "${#packaged_binaries[@]}" -gt 0 ]] || \
  fail "DEB does not contain an executable under /usr/bin."
[[ "${#packaged_binaries[@]}" -eq 1 ]] || {
  printf 'Executables found in DEB:\n%s\n' "${packaged_binaries[*]}" >&2
  fail "DEB contains more than one executable under /usr/bin; the Reader entry point is ambiguous."
}
binary="${packaged_binaries[0]}"
echo "Packaged Reader executable: /usr/bin/$(basename "$binary")"
file "$binary"

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
