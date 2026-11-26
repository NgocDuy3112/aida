#!/usr/bin/env bash
# Aliases the distro Tesseract libraries to the names the Tesseract 5.2.0 NuGet
# interop asks for on Linux. The interop probes "<assembly dir>/x64" for
# libtesseract50.so / libleptonica-1.82.0.so and the runtime probes the assembly
# dir for libdl.so, none of which exist under those names on a stock distro.
#
# Sonames are read from ldconfig rather than hard-coded, so a tesseract upgrade
# that renames the library fails here with a clear message instead of surfacing
# later as a DllNotFoundException at OCR time.
#
# Usage: scripts/link-tesseract-libs.sh <directory containing aida.Tests.dll>
set -Eeuo pipefail

assemblies_dir="${1:-}"
if [[ -z "$assemblies_dir" || ! -d "$assemblies_dir" ]]; then
  echo "usage: $0 <assembly-output-directory>" >&2
  exit 2
fi

if [[ "$(uname -s)" != "Linux" ]]; then
  echo "error: Tesseract native aliases are only needed on Linux (found $(uname -s))" >&2
  exit 1
fi

command -v ldconfig >/dev/null || { echo "error: ldconfig not found" >&2; exit 1; }

# Accepts several candidate sonames (first hit wins): newer distros rename the
# leptonica soname (Ubuntu 26.04 ships libleptonica6 -> liblept.so.6), and this
# script should keep working when the CI runner image moves ahead of prod.
soname() {
  local name path
  for name in "$@"; do
    path="$(ldconfig -p | awk -v name="$name" '$1 == name { print $NF; exit }')"
    if [[ -n "$path" && -e "$path" ]]; then
      printf '%s\n' "$path"
      return 0
    fi
  done
  echo "error: none of [$*] found by ldconfig — install tesseract-ocr (tesseract 5 expected)" >&2
  exit 1
}

libtesseract="$(soname libtesseract.so.5)"
liblept="$(soname liblept.so.5 liblept.so.6)"
libdl="$(soname libdl.so.2)"

mkdir -p "$assemblies_dir/x64"
ln -sfn "$libtesseract" "$assemblies_dir/x64/libtesseract50.so"
ln -sfn "$liblept" "$assemblies_dir/x64/libleptonica-1.82.0.so"
ln -sfn "$libdl" "$assemblies_dir/libdl.so"

printf 'linked %s -> %s\n' "$assemblies_dir/x64/libtesseract50.so" "$libtesseract"
printf 'linked %s -> %s\n' "$assemblies_dir/x64/libleptonica-1.82.0.so" "$liblept"
printf 'linked %s -> %s\n' "$assemblies_dir/libdl.so" "$libdl"
