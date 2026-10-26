#!/usr/bin/env bash
# Publish one test project as Native AOT and run the native MTP host.
# Extra args are forwarded to the host (treenode filters, etc.).
#
# Output stays under the project's bin/<rid>/publish so AppContext.BaseDirectory
# still walks up to Interfold.slnx (BinarySizeGuardrail, DinD fixtures, …).
set -euo pipefail

csproj="${1:?usage: run-aot-test.sh <csproj> [mtp-args...]}"
shift

if [[ ! -f "$csproj" ]]; then
  echo "csproj not found: $csproj" >&2
  exit 1
fi

name="$(basename "${csproj%.csproj}")"
proj_dir="$(cd "$(dirname "$csproj")" && pwd)"

if [[ -n "${AOT_TEST_RID:-}" ]]; then
  rid="$AOT_TEST_RID"
else
  case "$(uname -s)" in
    Linux*) rid=linux-x64 ;;
    MINGW*|MSYS*|CYGWIN*) rid=win-x64 ;;
    Darwin*) rid=osx-arm64 ;;
    *) echo "Unknown OS; set AOT_TEST_RID" >&2; exit 1 ;;
  esac
fi

out="$proj_dir/bin/Release/net10.0/${rid}/publish"
echo "dotnet publish $csproj -r $rid -> $out"
dotnet publish "$csproj" \
  --configuration Release \
  --runtime "$rid" \
  --self-contained true \
  --output "$out"

exe="$out/$name"
if [[ -f "${exe}.exe" ]]; then
  exe="${exe}.exe"
fi
if [[ ! -f "$exe" ]]; then
  echo "AOT test host not found at $exe" >&2
  exit 1
fi
chmod +x "$exe" || true

echo "running $exe $*"
"$exe" "$@"
