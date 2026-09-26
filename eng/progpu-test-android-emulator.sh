#!/usr/bin/env bash
set -euo pipefail

# Consumes an already-running emulator. Never starts/stops an emulator, clears
# device logs, kills the adb server, or touches applications other than this sample.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
evidence="${PROGPU_ANDROID_EVIDENCE_ROOT:-${repo_root}/artifacts/progpu-native/android-x64-evidence}"
mkdir -p "${evidence}"
evidence="$(cd "${evidence}" && pwd)"
apk="${PROGPU_ANDROID_APK:-${evidence}/ProGPU.Samples-Signed.apk}"
wgpu_root="${PROGPU_ANDROID_WGPU_ROOT:-${repo_root}/artifacts/wgpu-native-android}"
verifier="${repo_root}/eng/progpu-verify-android-evidence.py"
adb_command="${PROGPU_ANDROID_ADB:-adb}"
serial="${PROGPU_ANDROID_SERIAL:-${ANDROID_SERIAL:-}}"
package="com.progpu.samples"
logcat_pid=""
device_ready=false

bounded() { python3 "${verifier}" run-command --timeout "$1" -- "${@:2}"; }
device() { bounded 15 "${adb_command}" -s "${serial}" "$@"; }
finish() {
  local status=$?
  trap - EXIT
  set +e
  if [[ "${device_ready}" == true ]]; then
    device logcat -b all -d -v threadtime > "${evidence}/logcat-final.txt" 2>&1
    device shell dumpsys activity activities > "${evidence}/activity-final.txt" 2>&1
    device shell dumpsys window > "${evidence}/window-final.txt" 2>&1
    device shell dumpsys package "${package}" > "${evidence}/package-final.txt" 2>&1
    device shell pidof "${package}" > "${evidence}/pid-final.txt" 2>&1
    if ((status != 0)); then
      device exec-out screencap -p > "${evidence}/failure-screenshot.png" 2> "${evidence}/failure-screenshot.log"
    fi
  fi
  if [[ -n "${logcat_pid}" ]]; then
    kill "${logcat_pid}" 2>/dev/null
    for attempt in {1..20}; do
      kill -0 "${logcat_pid}" 2>/dev/null || break
      sleep 0.1
    done
    kill -KILL "${logcat_pid}" 2>/dev/null
    wait "${logcat_pid}" 2>/dev/null
  fi
  if ((status != 0)); then
    printf 'FAIL: Android x64 runtime evidence incomplete (exit %s).\n' "${status}" > "${evidence}/runtime-status.txt"
  fi
  exit "${status}"
}
trap finish EXIT
printf 'STARTED: Android x64 UI-only emulator gate.\n' > "${evidence}/runtime-status.txt"
python3 "${verifier}" apk --apk "${apk}" --wgpu-root "${wgpu_root}" \
  --output "${evidence}/apk-runtime-validation.json"
bounded 15 "${adb_command}" devices -l > "${evidence}/adb-devices.txt"
if [[ -z "${serial}" ]]; then
  serial="$(awk '$2 == "device" { print $1 }' "${evidence}/adb-devices.txt")"
  [[ -n "${serial}" && "${serial}" != *$'\n'* ]] || { echo "Require exactly one online device, or PROGPU_ANDROID_SERIAL." >&2; exit 1; }
fi
[[ "$(device get-state | tr -d '\r')" == device ]] || { echo "Selected adb target is not online." >&2; exit 1; }
device_ready=true
{
  bounded 15 "${adb_command}" version
  printf 'serial=%s\n' "${serial}"
  device shell getprop
  device shell pm list features
} > "${evidence}/device-versions.txt" 2>&1
[[ "$(device shell getprop ro.product.cpu.abi | tr -d '\r')" == x86_64 ]] || { echo "Exact x86_64 primary ABI required." >&2; exit 1; }
kernel_qemu="$(device shell getprop ro.kernel.qemu | tr -d '\r')"
boot_qemu="$(device shell getprop ro.boot.qemu | tr -d '\r')"
[[ "${kernel_qemu}" == 1 || "${boot_qemu}" == 1 ]] || { echo "This gate requires a verified emulator discriminator." >&2; exit 1; }

bounded 60 "${adb_command}" -s "${serial}" install -r "${apk}" 2>&1 | tee "${evidence}/install.log"
device shell cmd package resolve-activity --brief \
  -a android.intent.action.MAIN -c android.intent.category.LAUNCHER "${package}" > "${evidence}/launcher.txt"
launcher="$(tr -d '\r' < "${evidence}/launcher.txt" | tail -n 1)"
[[ "${launcher}" == "${package}/"* && "${launcher}" != *[[:space:]]* ]] || { echo "Could not resolve the sample launcher." >&2; exit 1; }
device shell am force-stop "${package}"
# Only this long-lived logcat client is owned by the EXIT trap. -T 1 keeps the
# device's existing logs intact and PID matching excludes earlier app processes.
"${adb_command}" -s "${serial}" logcat -b all -v threadtime -T 1 > "${evidence}/logcat.txt" 2> "${evidence}/logcat-client.log" &
logcat_pid=$!
logcat_deadline=$((SECONDS + 15))
until [[ -s "${evidence}/logcat.txt" ]]; do
  kill -0 "${logcat_pid}" 2>/dev/null || { echo "Logcat collection failed to start." >&2; exit 1; }
  ((SECONDS < logcat_deadline)) || { echo "Logcat did not become ready within 15 seconds." >&2; exit 1; }
  sleep 1
done

deadline=$((SECONDS + 120))
launch_command() {
  local remaining=$((deadline - SECONDS))
  ((remaining > 0)) || { echo "120-second launch deadline expired." >&2; return 124; }
  bounded "${remaining}" "$@"
}
launch_device() {
  local remaining=$((deadline - SECONDS))
  ((remaining > 0)) || { echo "120-second launch deadline expired." >&2; return 124; }
  if ((remaining > 15)); then remaining=15; fi
  bounded "${remaining}" "${adb_command}" -s "${serial}" "$@"
}
# Cold .NET/shader startup may exceed a diagnostic command's 15-second cap.
# It still shares, rather than resets, the fixed overall launch deadline.
bounded "$((deadline - SECONDS))" "${adb_command}" -s "${serial}" shell am start -W -n "${launcher}" > "${evidence}/launch.txt" 2>&1
grep -Eq '^Status: ok[[:space:]]*$' "${evidence}/launch.txt" || { echo "Activity launch failed." >&2; exit 1; }
application_pid=""
while ((SECONDS < deadline)); do
  application_pid="$(launch_device shell pidof -s "${package}" 2>/dev/null | tr -d '\r' || true)"
  if [[ "${application_pid}" =~ ^[1-9][0-9]*$ ]]; then break; fi
  sleep 1
done
[[ "${application_pid}" =~ ^[1-9][0-9]*$ ]] || { echo "Sample process did not start." >&2; exit 1; }
printf '%s\n' "${application_pid}" > "${evidence}/application-pid.txt"
frame_ready=false
while ((SECONDS < deadline)); do
  kill -0 "${logcat_pid}" 2>/dev/null || { echo "Logcat collection stopped unexpectedly." >&2; exit 1; }
  current_pid="$(launch_device shell pidof -s "${package}" 2>/dev/null | tr -d '\r' || true)"
  [[ "${current_pid}" == "${application_pid}" ]] || { echo "Sample exited or restarted before qualification." >&2; exit 1; }
  if launch_command python3 "${verifier}" first-frame --log "${evidence}/logcat.txt" --pid "${application_pid}" --output "${evidence}/first-frame.json"; then
    frame_ready=true
    break
  else
    result=$?
    ((result == 1)) || exit "${result}"
  fi
  sleep 1
done
[[ "${frame_ready}" == true ]] || { echo "No valid Vulkan first frame within 120 seconds." >&2; exit 1; }
capture_surface_state() {
  launch_device shell dumpsys SurfaceFlinger --list > "${evidence}/surfaceflinger-$1-layers.txt" 2>&1
  launch_device shell dumpsys SurfaceFlinger > "${evidence}/surfaceflinger-$1.txt" 2>&1
}
verify_live_log() {
  kill -0 "${logcat_pid}" 2>/dev/null || { echo "Logcat collection stopped unexpectedly." >&2; return 1; }
  # The live capture preserves errors even if Android's circular buffer rotates.
  launch_command python3 "${verifier}" first-frame --log "${evidence}/logcat.txt" --pid "${application_pid}" --output "${evidence}/first-frame.json"
}
# Present returning is not display completion. Poll actual opaque client pixels
# inside the SAME launch deadline; never accept elapsed time or a PNG container.
screenshot_ready=false
screenshot_attempt=0
while ((SECONDS < deadline)); do
  verify_live_log
  [[ "$(launch_device shell pidof -s "${package}" | tr -d '\r')" == "${application_pid}" ]] || { echo "Sample exited during screenshot polling." >&2; exit 1; }
  launch_device shell dumpsys activity activities > "${evidence}/activity.txt"
  launch_device shell dumpsys window > "${evidence}/window.txt"
  launch_device exec-out screencap -p > "${evidence}/screenshot.png" 2> "${evidence}/screenshot.log"
  launch_device shell dumpsys window > "${evidence}/window-after-screenshot.txt"
  screenshot_attempt=$((screenshot_attempt + 1))
  if launch_command python3 "${verifier}" screenshot --png "${evidence}/screenshot.png" \
      --activity "${evidence}/activity.txt" --window "${evidence}/window.txt" \
      --window-after "${evidence}/window-after-screenshot.txt" --output "${evidence}/screenshot.json"; then
    screenshot_result=0
  else
    screenshot_result=$?
  fi
  printf 'attempt=%s elapsed=%s result=%s\n' "${screenshot_attempt}" "$((120 - deadline + SECONDS))" "${screenshot_result}" >> "${evidence}/screenshot-attempts.txt"
  if ((screenshot_attempt == 1)); then
    cp "${evidence}/screenshot.png" "${evidence}/screenshot-initial.png"
    if [[ -f "${evidence}/screenshot.json" ]]; then cp "${evidence}/screenshot.json" "${evidence}/screenshot-initial.json"; fi
    cp "${evidence}/window.txt" "${evidence}/window-initial.txt"
    cp "${evidence}/window-after-screenshot.txt" "${evidence}/window-initial-after-screenshot.txt"
    capture_surface_state initial
  fi
  # Invalid images, lost focus/ANR dialogs and source errors are never retried
  # into success. Only a valid focused image lacking content is pending (1).
  ((screenshot_result <= 1)) || exit "${screenshot_result}"
  launch_device logcat -b all -d -v threadtime > "${evidence}/logcat-after-screenshot.txt"
  verify_live_log
  launch_command python3 "${verifier}" first-frame --log "${evidence}/logcat-after-screenshot.txt" --pid "${application_pid}" --output "${evidence}/first-frame.json"
  if ((screenshot_result == 0)); then
    screenshot_ready=true
    break
  fi
  sleep 1
done
[[ "${screenshot_ready}" == true ]] || { echo "No meaningful gallery pixels within 120-second launch deadline." >&2; exit 1; }
capture_surface_state final
[[ "$(launch_device shell pidof -s "${package}" | tr -d '\r')" == "${application_pid}" ]] || { echo "Sample exited during screenshot capture." >&2; exit 1; }
launch_device logcat -b all -d -v threadtime > "${evidence}/logcat-after-screenshot.txt"
verify_live_log
launch_command python3 "${verifier}" first-frame --log "${evidence}/logcat-after-screenshot.txt" --pid "${application_pid}" --output "${evidence}/first-frame.json"
((SECONDS <= deadline)) || { echo "120-second launch deadline expired." >&2; exit 1; }
printf 'PASS: x64 Vulkan frame, live resumed and focused sample, and distributed interior screenshot content captured. Visual inspection still required; no media/native-engine or hardware-performance qualification.\n' > "${evidence}/runtime-status.txt"
echo "Android x64 UI runtime evidence captured at ${evidence}; inspect screenshot for visible sample fidelity."
