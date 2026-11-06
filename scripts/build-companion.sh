#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
: "${DEVKITPRO:?Use a devkitPro Switch development environment}"
to_shell_path() {
  if command -v cygpath >/dev/null 2>&1 && [[ "$1" == *:* ]]; then cygpath -u "$1"; else printf '%s\n' "$1"; fi
}
export DEVKITPRO=$(to_shell_path "$DEVKITPRO")
export PATH="${DEVKITPRO}/tools/bin:${DEVKITPRO}/devkitA64/bin:${DEVKITPRO}/portlibs/switch/bin:${PATH}"
python_command=${SWITCHDESK_PYTHON:-python3}
python_command=$(to_shell_path "$python_command")
source_directory=$(to_shell_path "${SWITCHDESK_SOURCE_DIR:-vendor/sphaira}")
build_directory=$(to_shell_path "${SWITCHDESK_BUILD_DIR:-vendor/build}")
output_directory=$(to_shell_path "${SWITCHDESK_OUTPUT_DIR:-artifacts/Switch}")
if [[ "${SWITCHDESK_OFFLINE:-0}" == 1 && "${SWITCHDESK_SKIP_SDK_BUILD:-0}" != 1 ]]; then
  echo "Offline target builds require an existing dedicated SDK and SWITCHDESK_SKIP_SDK_BUILD=1." >&2
  exit 1
fi
prepare_args=(--destination "$source_directory")
if [[ "${SWITCHDESK_OFFLINE:-0}" == 1 ]]; then prepare_args+=(--offline); fi
"$python_command" scripts/prepare-companion.py "${prepare_args[@]}"
if [[ "${SWITCHDESK_SKIP_SDK_BUILD:-0}" != 1 ]]; then
  sdk_args=(--build)
  if [[ -n "${SWITCHDESK_SDK_SOURCE_ROOT:-}" ]]; then sdk_args+=(--source-root "$SWITCHDESK_SDK_SOURCE_ROOT"); fi
  "$python_command" scripts/prepare-sdk.py "${sdk_args[@]}"
fi
version=$("$python_command" -c 'import json;print(json.load(open("build-info.json"))["version"])')
identity=$("$python_command" -c 'import json;print(json.load(open("build-info.json"))["sourceId"])')
export SOURCE_DATE_EPOCH=$("$python_command" -c 'import json;print(json.load(open("version.json"))["sourceDateEpoch"])')
cmake -S "$source_directory" -B "$build_directory" -C vendor/dependencies.cmake -G Ninja \
  -DCMAKE_POLICY_VERSION_MINIMUM=3.5 -DCMAKE_BUILD_TYPE=MinSizeRel -Dsphaira_VERSION_OVERRIDE="SwitchDesk-${version}+${identity}" \
  -DSWITCHDESK_SOURCE_ID="$identity" \
  -DENABLE_NVJPG=OFF -DENABLE_NTFS=OFF -DENABLE_EXT4=OFF \
  -DENABLE_LIBUSBHSFS=OFF -DENABLE_LIBUSBDVD=OFF -DENABLE_FTPSRV=OFF -DENABLE_LIBHAZE=OFF \
  -DENABLE_DEVOPTAB_NFS=OFF -DENABLE_DEVOPTAB_SMB2=OFF -DENABLE_DEVOPTAB_SFTP=OFF
cmake --build "$build_directory" --parallel "${SWITCHDESK_BUILD_JOBS:-4}"
mkdir -p "$output_directory/switch/SwitchDesk"
cp "$build_directory/sphaira.nro" "$output_directory/switch/SwitchDesk/SwitchDesk.nro"
cp build-info.json "$output_directory/"
