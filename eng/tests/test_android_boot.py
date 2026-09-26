#!/usr/bin/env python3
"""Offline boot-state/process tests; never start an Android emulator."""

import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import time
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location("android_boot", Path(__file__).resolve().parents[1] / "progpu-run-android-emulator.py")
BOOT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BOOT)
HOME = "com.android.launcher3/.uioverrides.QuickstepLauncher"
FULL_HOME = "com.android.launcher3/com.android.launcher3.uioverrides.QuickstepLauncher"
PROPERTIES = {"boot_completed": "1", "device_provisioned": "1", "user_setup_complete": "1", "abi": "x86_64"}
# Representative ordinary Android 15 format from run36238216481's actual
# visible-activity dump. HOME identity/state here are synthetic positives, not
# evidence that the failed emulator itself completed a healthy boot.
ACTIVITY = f"""ACTIVITY MANAGER ACTIVITIES (dumpsys activity activities)
    topResumedActivity=ActivityRecord{{abc u0 {HOME} t7}}
    * Hist  #0: ActivityRecord{{abc u0 {HOME} t7}}
      state=RESUMED
      mVisibleRequested=true mVisible=true mClientVisible=true reportedDrawn=true reportedVisible=true
      mNumInterestingWindows=1 mNumDrawnWindows=1 allDrawn=true lastAllDrawn=false)
      startingData=null firstWindowDrawn=true mIsExiting=false
      nowVisible=true lastVisibleTime=-3s865ms
"""
WINDOW = f"""WINDOW MANAGER LAST ANR (dumpsys window lastanr)
  <no ANR has occurred since boot>
  mCurrentFocus=Window{{72459d u0 {FULL_HOME}}}
    isKeyguardShowing=false
  Window #7 Window{{72459d u0 {FULL_HOME}}}:
    mHasSurface=true isReadyForDisplay()=true mWindowRemovalAllowed=false
    WindowStateAnimator{{a798839 {FULL_HOME}}}:
      Surface: shown=true    mForceSeamlesslyRotate=false seamlesslyRotate: pending=null
    isOnScreen=true
    isVisible=true
"""


class AndroidBootTests(unittest.TestCase):
    def test_ordinary_dump_without_optional_draw_state_is_ready(self):
        result = BOOT.boot_ready(PROPERTIES, HOME, ACTIVITY, WINDOW, "")
        self.assertEqual(FULL_HOME, result["home"])
        self.assertEqual("72459d", result["window"])
        self.assertNotIn("mDrawState", WINDOW)

    def test_brief_home_metadata_and_fully_qualified_component(self):
        for home in ("priority=0 preferredOrder=0 match=0x108000 specificIndex=-1 isDefault=true\n" + HOME, FULL_HOME):
            with self.subTest(home=home):
                self.assertIsNotNone(BOOT.boot_ready(PROPERTIES, home, ACTIVITY, WINDOW, ""))

    def test_boot_completion_alone_is_not_ready(self):
        for key in ("boot_completed", "device_provisioned", "user_setup_complete"):
            for value in ("", "0", "null"):
                with self.subTest(key=key, value=value):
                    self.assertIsNone(BOOT.boot_ready({**PROPERTIES, key: value}, HOME, ACTIVITY, WINDOW, ""))

    def test_unresolved_ambiguous_or_different_home_is_pending(self):
        for home in ("No activity found", HOME + "\n" + HOME, "com.android.settings/.FallbackHome"):
            with self.subTest(home=home):
                self.assertIsNone(BOOT.boot_ready(PROPERTIES, home, ACTIVITY, WINDOW, ""))

    def test_resumed_identity_and_each_activity_draw_acknowledgement_are_required(self):
        variants = [ACTIVITY.replace("topResumedActivity", "mLastPausedActivity"),
                    ACTIVITY.replace("topResumedActivity=ActivityRecord{abc u0 " + HOME,
                                     "topResumedActivity=ActivityRecord{abc u0 com.android.settings/.FallbackHome")]
        variants += [ACTIVITY.replace(flag + "=true", flag + "=false")
                     for flag in ("reportedDrawn", "allDrawn", "firstWindowDrawn", "nowVisible")]
        for activity in variants:
            with self.subTest(activity=activity):
                self.assertIsNone(BOOT.boot_ready(PROPERTIES, HOME, activity, WINDOW, ""))

    def test_visible_drawn_window_must_be_the_exact_focused_window(self):
        variants = [WINDOW.replace("mCurrentFocus=Window{72459d", "mCurrentFocus=Window{999999"),
                    WINDOW.replace("isKeyguardShowing=false", "isKeyguardShowing=true"),
                    WINDOW.replace("Surface: shown=true", "Surface: shown=true mDrawState=DRAW_PENDING")]
        variants += [WINDOW.replace(flag + "=true", flag + "=false")
                     for flag in ("mHasSurface", "isReadyForDisplay()", "isOnScreen", "isVisible", "shown")]
        for window in variants:
            with self.subTest(window=window):
                self.assertIsNone(BOOT.boot_ready(PROPERTIES, HOME, ACTIVITY, window, ""))
        self.assertIsNotNone(BOOT.boot_ready(PROPERTIES, HOME, ACTIVITY,
                                           WINDOW.replace("Surface: shown=true", "Surface: shown=true mDrawState=HAS_DRAWN"), ""))

    def test_draw_state_cannot_be_borrowed_from_another_activity_or_window(self):
        other_activity = ACTIVITY.replace("abc", "def").replace(HOME, "com.other/.Main")
        hidden = ACTIVITY.replace("reportedDrawn=true", "reportedDrawn=false") + other_activity
        self.assertIsNone(BOOT.boot_ready(PROPERTIES, HOME, hidden, WINDOW, ""))
        other_window = WINDOW[WINDOW.index("  Window #7"):].replace("72459d", "999999")
        self.assertIsNone(BOOT.boot_ready(PROPERTIES, HOME, ACTIVITY,
                                         WINDOW.replace("shown=true", "shown=false") + other_window, ""))

    def test_actual_launcher_anr_excerpt_is_rejected_even_with_ready_positive_state(self):
        fixture = Path(__file__).parent / "fixtures/android-x64-launcher-anr.txt"
        with self.assertRaisesRegex(ValueError, "ANR"):
            BOOT.boot_ready(PROPERTIES, HOME, ACTIVITY, fixture.read_text() + WINDOW, "")

    def test_cumulative_anr_or_fatal_log_is_never_pending(self):
        for message in ("I am_anr: [0,1317,com.android.launcher3]", "E ActivityManager: ANR in com.android.launcher3",
                        "E AndroidRuntime: FATAL EXCEPTION: main", "F libc: Fatal signal 11"):
            with self.subTest(message=message), self.assertRaisesRegex(ValueError, "ANR or fatal"):
                BOOT.boot_ready({}, HOME, "", "", message)

    def test_wrong_abi_is_a_hard_failure(self):
        with self.assertRaisesRegex(ValueError, "x86_64"):
            BOOT.boot_ready({**PROPERTIES, "abi": "arm64-v8a"}, HOME, ACTIVITY, WINDOW, "")

    def test_missing_abi_never_admits_an_otherwise_ready_device(self):
        for value in (None, ""):
            with self.subTest(value=value):
                self.assertIsNone(BOOT.boot_ready({**PROPERTIES, "abi": value}, HOME, ACTIVITY, WINDOW, ""))

    def test_profile_resources_replace_duplicates_without_changing_other_fields(self):
        text = "hw.ramSize=512\nhw.gpu.mode=auto\nhw.ramSize=1024\nhw.cpu.ncore=8\nhw.keyboard=no\n"
        result = BOOT.configure_avd(text)
        self.assertEqual(1, result.count("hw.ramSize="))
        self.assertIn("hw.ramSize=2048M\n", result)
        self.assertIn("hw.cpu.ncore=2\n", result)
        self.assertIn("hw.heapSize=512M\n", result)
        self.assertIn("hw.gpu.mode=auto\nhw.keyboard=no\n", result)

    def test_remaining_budget_never_resets(self):
        with patch.object(BOOT.time, "monotonic", return_value=397.5):
            self.assertEqual(2.5, BOOT.remaining(400))
        with patch.object(BOOT.time, "monotonic", return_value=400):
            with self.assertRaises(TimeoutError):
                BOOT.remaining(400)
        self.assertEqual(300, BOOT.BOOT_SECONDS)

    def test_owned_command_timeout_does_not_terminate_an_unrelated_process(self):
        unrelated = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"], start_new_session=True)
        try:
            with self.assertRaises(subprocess.TimeoutExpired):
                BOOT.command([sys.executable, "-c", "import time; time.sleep(30)"], dict(os.environ), timeout=0.1)
            self.assertIsNone(unrelated.poll())
        finally:
            BOOT.stop_owned(unrelated)
        self.assertIsNotNone(unrelated.returncode)
        BOOT.stop_owned(unrelated)

    def test_command_failure_and_stderr_provenance(self):
        output = BOOT.command([sys.executable, "-c", "import sys; print('version', file=sys.stderr)"], dict(os.environ), timeout=5, merge_error=True)
        self.assertEqual(b"version\n", output)
        with self.assertRaisesRegex(RuntimeError, "intentional"):
            BOOT.command([sys.executable, "-c", "import sys; print('intentional', file=sys.stderr); sys.exit(3)"], dict(os.environ), timeout=5)

    def test_exited_command_leader_cannot_leave_a_descendant_holding_pipes(self):
        parent = ("import subprocess,sys; child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(30)']); "
                  "print(child.pid, flush=True)")
        with self.assertRaises(subprocess.TimeoutExpired) as expired:
            BOOT.command([sys.executable, "-c", parent], dict(os.environ), timeout=0.3)
        child_pid = int(expired.exception.output.strip())
        deadline = time.monotonic() + 2
        while True:
            state = subprocess.run(["ps", "-p", str(child_pid), "-o", "stat="], capture_output=True, text=True, timeout=1).stdout.strip()
            if not state or state.startswith("Z"):
                break
            self.assertLess(time.monotonic(), deadline, f"Owned descendant remains running: {state}")
            time.sleep(0.01)

    def test_lifecycle_rejects_non_ci_execution_before_starting_processes(self):
        with patch.dict(os.environ, {"GITHUB_ACTIONS": "false"}), patch.object(BOOT.subprocess, "Popen") as start:
            with self.assertRaisesRegex(ValueError, "Actions lane"):
                BOOT.main()
            start.assert_not_called()


if __name__ == "__main__":
    unittest.main()
