#!/usr/bin/env bash
# Smoke test for the Tesseract install inside the app image (Dockerfile final stage).
# Builds the image, then verifies from inside it that
#   1. the tesseract binary runs (libtesseract + leptonica load),
#   2. vie and eng models are installed and reachable via TESSDATA_PREFIX,
#   3. the NuGet interop aliases (/app/x64/libtesseract50.so, libleptonica-1.82.0.so)
#      exist and their sonames still resolve — a distro upgrade that renames
#      libtesseract.so.5 / liblept.so.5 breaks OCR silently otherwise.
#
# Usage: scripts/smoke-ocr.sh           # build, check, remove the image
#        scripts/smoke-ocr.sh --keep    # keep the image for inspection
# Env: CONTAINER_ENGINE (default podman), OCR_SMOKE_TAG (default aida-ocr-smoke:local)
set -Eeuo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENGINE="${CONTAINER_ENGINE:-podman}"
TAG="${OCR_SMOKE_TAG:-aida-ocr-smoke:local}"

keep=0
case "${1:-}" in
  "") ;;
  --keep) keep=1 ;;
  *) echo "usage: $0 [--keep]" >&2; exit 2 ;;
esac

command -v "$ENGINE" >/dev/null || { echo "error: container engine '$ENGINE' not found" >&2; exit 1; }

cleanup() {
  if [[ $keep -eq 1 ]]; then
    echo "==> keeping image $TAG"
  else
    "$ENGINE" rmi -f "$TAG" >/dev/null 2>&1 || true
    echo "==> removed image $TAG"
  fi
}
trap cleanup EXIT

echo "==> $ENGINE build -t $TAG ($REPO_ROOT/Dockerfile)"
"$ENGINE" build -f "$REPO_ROOT/Dockerfile" -t "$TAG" "$REPO_ROOT"

echo "==> checking tesseract inside $TAG"
"$ENGINE" run --rm -i --entrypoint bash "$TAG" -s <<'CONTAINER_EOF'
set -Eeuo pipefail
fail() { echo "FAIL: $*" >&2; exit 1; }

tesseract --version >/dev/null 2>&1 || fail "tesseract binary does not run (libtesseract/leptonica missing?)"
echo "ok: tesseract --version -> $(tesseract --version 2>&1 | head -1)"

langs="$(tesseract --list-langs 2>/dev/null || fail "tesseract --list-langs failed")"
echo "ok: tesseract --list-langs -> $(echo "$langs" | tail -n +2 | tr '\n' ' ')"
grep -qx vie <<<"$langs" || fail "vie model missing from TESSDATA_PREFIX=$TESSDATA_PREFIX"
grep -qx eng <<<"$langs" || fail "eng model missing from TESSDATA_PREFIX=$TESSDATA_PREFIX"

for alias in /app/x64/libtesseract50.so /app/x64/libleptonica-1.82.0.so; do
  [[ -L "$alias" ]] || fail "$alias is not a symlink (Dockerfile alias step skipped?)"
  target="$(readlink -f "$alias")"
  [[ -n "$target" && -f "$target" ]] || fail "$alias -> '$target' does not resolve (distro soname changed?)"
  if ldd "$alias" 2>&1 | grep -q 'not found'; then
    fail "$alias has unresolved dependencies: $(ldd "$alias" 2>&1 | grep 'not found' | tr '\n' ' ')"
  fi
  echo "ok: $alias -> $target (dependencies resolve)"
done
CONTAINER_EOF

echo "==> OCR image smoke test passed"
