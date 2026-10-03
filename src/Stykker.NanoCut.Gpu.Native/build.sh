#!/usr/bin/env bash
# Builds the native CUDA library (libnanocut_gpu.so, or .dylib on macOS) that Stykker.NanoCut.Gpu calls through
# LibraryImport. The library is optional and is not part of "dotnet build": CI has neither a GPU nor nvcc. When
# bin/libnanocut_gpu.so exists, the Stykker.NanoCut.Gpu project copies it to its output; when it does not, the
# managed CPU backend is all that is available and CudaRuntime reports why.
#
# Usage: ./build.sh [compute capability without the dot] [Release|Debug]
#        ./build.sh            # sm_120 (Blackwell), Release
#        ./build.sh 89 Debug   # sm_89 (Ada), with line info
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
arch="${1:-120}"
configuration="${2:-Release}"

case "$(uname -s)" in
    Darwin) target="$here/bin/libnanocut_gpu.dylib" ;;
    *)      target="$here/bin/libnanocut_gpu.so" ;;
esac

mkdir -p "$here/bin"

if ! command -v nvcc >/dev/null 2>&1; then
    echo "nvcc not found on the PATH; install the CUDA toolkit or export CUDA_PATH and add \$CUDA_PATH/bin." >&2
    exit 1
fi

if [ "$configuration" = "Debug" ]; then
    optimise="-O0 -lineinfo"
else
    optimise="-O3"
fi

# SASS for the card that runs it, plus PTX for compute_75 so older and future cards still load the library.
set -x
nvcc $optimise -shared -std=c++17 -fPIC \
    -gencode "arch=compute_${arch},code=sm_${arch}" \
    -gencode arch=compute_75,code=compute_75 \
    -o "$target" "$here/zmap.cu"
set +x

echo "built $target (sm_${arch} + compute_75 PTX)"
echo "Rebuild Stykker.NanoCut.Gpu to copy it next to the managed assembly:"
echo "  dotnet build src/Stykker.NanoCut.Gpu -c Release"
