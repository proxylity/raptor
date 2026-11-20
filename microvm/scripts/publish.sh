#!/usr/bin/env bash
#
# Compiles the MicroVM app to one native Linux executable (NativeAOT) and stages it with its Dockerfile in
# microvm/build/image/, which is what the MicroVM image is built from. Also publishes the native raptor2 CLI
# for this machine into microvm/build/cli/.
#
# Usage: microvm/scripts/publish.sh
#   RID=linux-x64 (default) or linux-arm64      MicroVM architecture
#   WORK_DIR=~/raptor2-build                    build in a Linux path (default; avoids /mnt/c slowness and
#                                               Windows bin/obj folders)
#   SKIP_CLI=1                                  don't publish the CLI
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
RID="${RID:-linux-x64}"
WORK_DIR="${WORK_DIR:-$HOME/raptor2-build}"
CPP_LINKER="${CPP_LINKER:-$(command -v clang >/dev/null && echo clang || echo gcc)}"
STAGE="$ROOT/microvm/build/image"

mkdir -p "$WORK_DIR"
tar -C "$ROOT" --exclude=bin --exclude=obj -cf - src microvm/image | tar -C "$WORK_DIR" -xf -

echo "Publishing MicroVM app ($RID, NativeAOT, linker $CPP_LINKER)..."
(cd "$WORK_DIR" && dotnet publish src/raptor2-microvm -c Release -r "$RID" \
  -p:CppCompilerAndLinker="$CPP_LINKER" -o "$WORK_DIR/publish/vm-$RID" --nologo -v q)

rm -rf "$STAGE"
mkdir -p "$STAGE"
cp "$WORK_DIR/publish/vm-$RID/raptor2-microvm" "$STAGE/"
cp "$ROOT/microvm/image/Dockerfile" "$STAGE/"
chmod +x "$STAGE/raptor2-microvm"

# al2023-minimal has glibc 2.34. A binary linked on a newer distro still runs there if it only needs symbols up to it.
MAX_GLIBC=$(objdump -T "$STAGE/raptor2-microvm" | grep -o 'GLIBC_[0-9.]*' | sort -uV | tail -1)
echo "Highest glibc symbol version required: ${MAX_GLIBC}"
if [[ "$(printf '%s\n' "$MAX_GLIBC" GLIBC_2.34 | sort -V | tail -1)" != "GLIBC_2.34" ]]; then
  echo "The executable needs a newer glibc than al2023 provides. Build on an older distro (or in an al2023 container)." >&2
  exit 1
fi
ls -la "$STAGE"

if [[ "${SKIP_CLI:-0}" != 1 ]]; then
  HOST_RID="linux-$(uname -m | sed -e 's/x86_64/x64/' -e 's/aarch64/arm64/')"
  echo "Publishing raptor2 CLI ($HOST_RID, NativeAOT)..."
  (cd "$WORK_DIR" && dotnet publish src/raptor2-cli -c Release -r "$HOST_RID" \
    -p:CppCompilerAndLinker="$CPP_LINKER" -o "$WORK_DIR/publish/cli-$HOST_RID" --nologo -v q)
  mkdir -p "$ROOT/microvm/build/cli"
  cp "$WORK_DIR/publish/cli-$HOST_RID/raptor2" "$ROOT/microvm/build/cli/"
  # The WireGuard client (wg://) loads libsodium from next to the executable.
  cp "$WORK_DIR/publish/cli-$HOST_RID/"libsodium.* "$ROOT/microvm/build/cli/" 2>/dev/null || true
  ls -la "$ROOT/microvm/build/cli/raptor2"
fi
