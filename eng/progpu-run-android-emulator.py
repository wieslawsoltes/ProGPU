#!/usr/bin/env python3
"""Own one CI emulator; admit a drawn HOME before running the existing APK gate."""

import json
import os
from pathlib import Path
import platform
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time


BOOT_SECONDS = 300
SERIAL = "emulator-5554"
AVD_NAME = "ProGpuX64Rendering"
IMAGE = "system-images;android-35;default;x86_64"
CONFIG = {"hw.cpu.ncore": "2", "hw.ramSize": "2048M", "hw.heapSize": "512M"}
OPTIONS = ["-no-window", "-gpu", "swiftshader", "-no-snapshot", "-noaudio", "-no-boot-anim",
           "-camera-back", "none", "-camera-front", "none", "-accel", "on"]


def component(value):
    match = re.fullmatch(r"([\w.$]+)/([\w.$]+)", value.strip(), re.ASCII)
    if not match:
        return None
    package, name = match.groups()
    return package + "/" + (package + name if name.startswith(".") else name)


def records(text, pattern):
    matches = list(re.finditer(pattern, text, re.MULTILINE))
    for index, match in enumerate(matches):
        yield match, text[match.end():matches[index + 1].start() if index + 1 < len(matches) else len(text)]


def reject_boot_failures(window, log):
    # A fresh owned device has no acceptable historical ANR. Do not dismiss one
    # or mistake its historical window snapshot for the current focused window.
    if re.search(r"^\s*ANR time:|Application Not Responding:", window, re.MULTILINE):
        raise ValueError("Emulator boot recorded an ANR")
    if re.search(r"\b(?:am_anr\s*:|ANR in |FATAL EXCEPTION|Fatal signal)", log):
        raise ValueError("Emulator boot log contains an ANR or fatal failure")


def boot_ready(properties, home, activity, window, log):
    reject_boot_failures(window, log)
    if properties.get("abi") not in (None, "", "x86_64"):
        raise ValueError("Booted device does not have the required x86_64 ABI")
    if any(properties.get(key) != "1" for key in ("boot_completed", "device_provisioned", "user_setup_complete")):
        return None
    # --brief may precede the component with priority/match metadata.
    candidates = [component(line) for line in home.splitlines() if component(line) is not None]
    if len(candidates) != 1:
        return None
    resolved = candidates[0]
    resumed = re.findall(r"(?:topResumedActivity|mResumedActivity)[^\n]*ActivityRecord\{[^\n}]*\bu0\s+(\S+)", activity)
    if not resumed or any(component(value) != resolved for value in resumed):
        return None
    focused = re.findall(r"^\s*mCurrentFocus=Window\{([0-9a-f]+) u0 ([^}]+)\}", window, re.MULTILINE)
    if len(focused) != 1 or component(focused[0][1]) != resolved:
        return None
    if not re.search(r"\bisKeyguardShowing=false\b", window) or re.search(r"\bisKeyguardShowing=true\b", window):
        return None
    activities = [body for match, body in records(activity, r"^\s*\* Hist\s+#\d+: ActivityRecord\{[^\n}]*\bu0\s+(\S+)[^\n]*")
                  if component(match[1]) == resolved]
    if len(activities) != 1 or not all(re.search(r"\b" + flag + r"=true\b", activities[0])
                                     for flag in ("reportedDrawn", "allDrawn", "firstWindowDrawn", "nowVisible")):
        return None
    windows = [body for match, body in records(window, r"^\s*Window #\d+ Window\{([0-9a-f]+) u0 ([^}]+)\}:")
               if match[1] == focused[0][0] and component(match[2]) == resolved]
    if len(windows) != 1:
        return None
    body = windows[0]
    if not all(re.search(pattern, body) for pattern in
               (r"\bmHasSurface=true\b", r"\bisReadyForDisplay\(\)=true\b", r"\bisOnScreen=true\b",
                r"\bisVisible=true\b", r"\bSurface:\s+shown=true\b")):
        return None
    # Ordinary Android 15 dumps omit mDrawState. The required ActivityRecord
    # draw acknowledgements above remain authoritative; an explicit pending
    # state in a verbose dump must still reject the candidate.
    states = re.findall(r"\bmDrawState=(\w+)", body)
    if any(state != "HAS_DRAWN" for state in states):
        return None
    return {"home": resolved, "window": focused[0][0], "properties": properties,
            "scope": "Drawn focused HOME readiness only; not sample rendering qualification"}


def configure_avd(text):
    # Retain every unrelated profile field, replacing duplicate resource keys
    # rather than depending on an INI reader's first/last duplicate policy.
    lines = [line for line in text.splitlines() if line.partition("=")[0].strip() not in CONFIG]
    return "\n".join(lines + [f"{key}={value}" for key, value in CONFIG.items()]) + "\n"


def remaining(deadline):
    value = deadline - time.monotonic()
    if value <= 0:
        raise TimeoutError("300-second emulator boot deadline expired")
    return value


def stop_owned(process, force=False):
    if process is None or process.poll() is not None:
        return
    try:
        os.killpg(process.pid, signal.SIGKILL if force else signal.SIGTERM)
    except ProcessLookupError:
        process.wait(timeout=5)
        return
    try:
        process.wait(timeout=15)
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGKILL)
        process.wait(timeout=5)


def command(arguments, env, timeout=None, input_text=None, merge_error=False):
    process = subprocess.Popen(arguments, env=env, stdin=subprocess.PIPE if input_text is not None else subprocess.DEVNULL,
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT if merge_error else subprocess.PIPE, start_new_session=True)
    try:
        output, error = process.communicate(input_text.encode() if input_text is not None else None, timeout=timeout)
    except BaseException:
        # Only this subprocess group is owned. Never kill the adb server or an
        # emulator selected solely by a possibly stale device serial.
        stop_owned(process, force=True)
        raise
    finally:
        for stream in (process.stdin, process.stdout, process.stderr):
            if stream is not None:
                stream.close()
    if process.returncode:
        raise RuntimeError(f"Command failed ({process.returncode}): {arguments!r}\n{(error if error is not None else output).decode(errors='replace')}")
    return output


def main():
    if os.environ.get("GITHUB_ACTIONS") != "true" or platform.system() != "Linux" or platform.machine() != "x86_64":
        raise ValueError("This lifecycle is restricted to the Linux x64 Actions lane")
    if not os.access("/dev/kvm", os.R_OK | os.W_OK):
        raise ValueError("Accessible KVM is required; software CPU emulation is not admitted")
    repo = Path(__file__).resolve().parent.parent
    evidence = Path(os.environ["PROGPU_ANDROID_EVIDENCE_ROOT"]).resolve()
    evidence.mkdir(parents=True, exist_ok=True)
    sdk = Path(os.environ["ANDROID_SDK_ROOT"])
    emulator = sdk / "emulator/emulator"
    adb = str(sdk / "platform-tools/adb")
    env = dict(os.environ)
    env["ANDROID_AVD_HOME"] = tempfile.mkdtemp(prefix="progpu-android-avd-")
    env["PROGPU_ANDROID_SERIAL"] = SERIAL
    avd_directory = Path(env["ANDROID_AVD_HOME"]) / (AVD_NAME + ".avd")
    # setup-android already owns command-line tools and accepted SDK licenses.
    # These are the stable-channel runtime packages previously installed by the
    # emulator action, not a new image, tool revision override or renderer mode.
    installation = command(["sdkmanager", "--install", "--channel=0", "emulator", "platforms;android-35", IMAGE], env, input_text="y\n", merge_error=True)
    (evidence / "emulator-install.txt").write_bytes(installation)
    devices = command([adb, "devices", "-l"], env, timeout=15).decode(errors="replace")
    (evidence / "emulator-devices-before.txt").write_text(devices)
    if any(line.strip() and not line.startswith("List of devices") for line in devices.splitlines()):
        raise ValueError("The owned CI lifecycle requires an unused adb device list")
    creation = command(["avdmanager", "create", "avd", "--name", AVD_NAME, "--package", IMAGE, "--device", "pixel_6"], env, input_text="no\n", merge_error=True)
    (evidence / "emulator-create.txt").write_bytes(creation)
    config = avd_directory / "config.ini"
    config.write_text(configure_avd(config.read_text()))
    shutil.copyfile(config, evidence / "emulator-config.ini")
    for name, arguments in (("emulator-version.txt", [str(emulator), "-version"]),
                            ("emulator-acceleration.txt", [str(emulator), "-accel-check"]),
                            ("android-installed-packages.txt", ["sdkmanager", "--list_installed"])):
        (evidence / name).write_bytes(command(arguments, env, timeout=15, merge_error=True))

    emulator_process = logcat_process = None
    admitted = False
    with (evidence / "emulator-launch.log").open("wb") as emulator_log, (evidence / "boot-logcat.txt").open("wb") as boot_log:
        try:
            deadline = time.monotonic() + BOOT_SECONDS
            emulator_process = subprocess.Popen([str(emulator), "-port", "5554", "-avd", AVD_NAME, *OPTIONS],
                                                env=env, stdout=emulator_log, stderr=subprocess.STDOUT, start_new_session=True)

            def device(*arguments):
                return command([adb, "-s", SERIAL, *arguments], env, timeout=min(15, remaining(deadline)))

            def capture(name, *arguments):
                value = device(*arguments)
                (evidence / name).write_bytes(value)
                return value.decode(errors="replace").strip()

            result = None
            while result is None:
                remaining(deadline)
                if emulator_process.poll() is not None:
                    raise RuntimeError("Owned emulator exited before HOME readiness")
                try:
                    connected = device("get-state").strip() == b"device"
                except RuntimeError:
                    connected = False
                if not connected:
                    time.sleep(min(1, remaining(deadline)))
                    continue
                if logcat_process is None:
                    logcat_process = subprocess.Popen([adb, "-s", SERIAL, "logcat", "-b", "all", "-v", "threadtime"],
                                                      env=env, stdout=boot_log, stderr=subprocess.STDOUT, start_new_session=True)
                if logcat_process.poll() is not None:
                    raise RuntimeError("Boot logcat collection stopped")
                reject_boot_failures("", (evidence / "boot-logcat.txt").read_text(errors="replace"))
                properties = {"boot_completed": capture("boot-completed.txt", "shell", "getprop", "sys.boot_completed"),
                              "abi": capture("boot-abi.txt", "shell", "getprop", "ro.product.cpu.abi")}
                if properties["boot_completed"] != "1":
                    time.sleep(min(1, remaining(deadline)))
                    continue
                properties["device_provisioned"] = capture("boot-provisioned.txt", "shell", "settings", "get", "global", "device_provisioned")
                properties["user_setup_complete"] = capture("boot-user-setup.txt", "shell", "settings", "get", "secure", "user_setup_complete")
                home = capture("boot-home.txt", "shell", "cmd", "package", "resolve-activity", "--brief", "-a", "android.intent.action.MAIN", "-c", "android.intent.category.HOME")
                activity = capture("boot-activity.txt", "shell", "dumpsys", "activity", "activities")
                window = capture("boot-window.txt", "shell", "dumpsys", "window")
                result = boot_ready(properties, home, activity, window, (evidence / "boot-logcat.txt").read_text(errors="replace"))
                with (evidence / "boot-attempts.txt").open("a") as attempts:
                    attempts.write(f"elapsed={BOOT_SECONDS - remaining(deadline):.3f} ready={result is not None}\n")
                if result is None:
                    time.sleep(min(1, remaining(deadline)))

            # Preserve the previous lane's animation configuration, only after
            # actual HOME readiness. No synthetic input or keyguard dismissal.
            for setting in ("window_animation_scale", "transition_animation_scale", "animator_duration_scale"):
                device("shell", "settings", "put", "global", setting, "0.0")
            capture("boot-input.txt", "shell", "dumpsys", "input")
            (evidence / "boot-screenshot.png").write_bytes(device("exec-out", "screencap", "-p"))
            final_activity = capture("boot-activity-final.txt", "shell", "dumpsys", "activity", "activities")
            final_window = capture("boot-window-final.txt", "shell", "dumpsys", "window")
            final_log = capture("boot-logcat-final.txt", "logcat", "-b", "all", "-d", "-v", "threadtime")
            reject_boot_failures(final_window, (evidence / "boot-logcat.txt").read_text(errors="replace"))
            result = boot_ready(properties, home, final_activity, final_window, final_log)
            if result is None or logcat_process.poll() is not None:
                raise RuntimeError("HOME lost readiness before APK installation")
            remaining(deadline)
            (evidence / "boot-ready.json").write_text(json.dumps(result, indent=2) + "\n")
            admitted = True
            # The existing probe still owns installation, its 120-second launch
            # deadline, process/focus/log checks and real screenshot-content gate.
            return subprocess.call(["bash", str(repo / "eng/progpu-test-android-emulator.sh")], env=env)
        finally:
            if emulator_process is not None and emulator_process.poll() is None and not admitted:
                # Failure evidence is cleanup, never additional time to qualify.
                for name, arguments in (("boot-failure-window.txt", ["shell", "dumpsys", "window"]),
                                        ("boot-failure-activity.txt", ["shell", "dumpsys", "activity", "activities"]),
                                        ("boot-failure.png", ["exec-out", "screencap", "-p"])):
                    try:
                        (evidence / name).write_bytes(command([adb, "-s", SERIAL, *arguments], env, timeout=15))
                    except (RuntimeError, OSError, subprocess.TimeoutExpired) as error:
                        (evidence / (name + ".error")).write_text(str(error))
            try:
                stop_owned(logcat_process)
            finally:
                stop_owned(emulator_process)


if __name__ == "__main__":
    def terminate(signum, frame):
        raise SystemExit(128 + signum)

    signal.signal(signal.SIGTERM, terminate)
    try:
        sys.exit(main())
    except (ValueError, RuntimeError, OSError, subprocess.TimeoutExpired) as error:
        print(f"Android emulator lifecycle rejected: {error}", file=sys.stderr)
        sys.exit(2)
