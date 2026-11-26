#!/usr/bin/env bash
# Runs the OCR integration test (xunit trait Integration=Ocr, gated by AIDA_OCR_IT=1)
# inside a Linux container, so the macOS host never has to provide Tesseract's native
# libraries. The repo is mounted read-only and copied inside the container, so the
# container never writes root-owned bin/obj into the checkout.
#
# Usage: scripts/ocr-it.sh            # OCR test only
#        OCR_IT_FILTER= dotnet test filter override (e.g. "" for the full suite)
set -Eeuo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENGINE="${CONTAINER_ENGINE:-podman}"
SDK_IMAGE="${OCR_IT_SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
FILTER="${OCR_IT_FILTER-Integration=Ocr}"

command -v "$ENGINE" >/dev/null || { echo "error: container engine '$ENGINE' not found" >&2; exit 1; }

echo "==> $ENGINE run $SDK_IMAGE (tesseract-ocr + vie/eng, AIDA_OCR_IT=1, filter '${FILTER:-<none>}')"

"$ENGINE" run --rm -i \
  -v "$REPO_ROOT:/src:ro" \
  -e AIDA_OCR_IT=1 \
  -e DOTNET_NOLOGO=1 \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e OCR_IT_FILTER="$FILTER" \
  "$SDK_IMAGE" bash -s <<'CONTAINER_EOF'
set -Eeuo pipefail
export DEBIAN_FRONTEND=noninteractive

apt-get update -qq
apt-get install -y -qq --no-install-recommends tesseract-ocr tesseract-ocr-vie tesseract-ocr-eng
tesseract --version | head -1

work="$(mktemp -d)"
tar -C /src --exclude=./bin --exclude=./obj --exclude=./.git --exclude=./.codegraph --exclude=./tests -cf - . | tar -C "$work" -xf -
mkdir -p "$work/tests"
tar -C /src/tests --exclude=./aida.Tests/bin --exclude=./aida.Tests/obj -cf - . | tar -C "$work/tests" -xf -
cd "$work"

dotnet build tests/aida.Tests/aida.Tests.csproj -v q --nologo
/src/scripts/link-tesseract-libs.sh tests/aida.Tests/bin/Debug/net10.0

# --logger console shows the test name, so a pass is visibly a real run and not the
# AIDA_OCR_IT silent skip.
logger='--logger=console;verbosity=normal'
if [[ -n "$OCR_IT_FILTER" ]]; then
  dotnet test tests/aida.Tests/aida.Tests.csproj --no-build --filter "$OCR_IT_FILTER" "$logger"
else
  dotnet test tests/aida.Tests/aida.Tests.csproj --no-build "$logger"
fi
CONTAINER_EOF
