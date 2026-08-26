#!/usr/bin/env python3
"""Inject one real Linux touchscreen contact through uinput."""

import argparse
import time

from evdev import AbsInfo, UInput, ecodes


def axis(maximum: int) -> AbsInfo:
    return AbsInfo(
        value=0,
        min=0,
        max=maximum,
        fuzz=0,
        flat=0,
        resolution=1,
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--x", type=int, default=400)
    parser.add_argument("--y", type=int, default=350)
    parser.add_argument("--screen-width", type=int, default=1440)
    parser.add_argument("--screen-height", type=int, default=900)
    parser.add_argument("--enumeration-seconds", type=float, default=2)
    parser.add_argument("--hold-seconds", type=float, default=1)
    args = parser.parse_args()

    capabilities = {
        ecodes.EV_KEY: [ecodes.BTN_TOUCH],
        ecodes.EV_ABS: [
            (ecodes.ABS_X, axis(args.screen_width - 1)),
            (ecodes.ABS_Y, axis(args.screen_height - 1)),
            (ecodes.ABS_MT_SLOT, axis(9)),
            (ecodes.ABS_MT_TRACKING_ID, axis(65535)),
            (ecodes.ABS_MT_POSITION_X, axis(args.screen_width - 1)),
            (ecodes.ABS_MT_POSITION_Y, axis(args.screen_height - 1)),
        ],
    }

    with UInput(
        capabilities,
        name="ProGPU integration touchscreen",
        bustype=ecodes.BUS_VIRTUAL,
        input_props=[ecodes.INPUT_PROP_DIRECT],
    ) as device:
        # Give Mutter and XWayland time to enumerate the new direct device.
        time.sleep(args.enumeration_seconds)
        write_contact(device, args.x, args.y, tracking_id=1, down=True)
        time.sleep(0.1)
        write_contact(
            device,
            args.x + 12,
            args.y + 8,
            tracking_id=1,
            down=True,
        )
        time.sleep(0.1)
        device.write(ecodes.EV_ABS, ecodes.ABS_MT_TRACKING_ID, -1)
        device.write(ecodes.EV_KEY, ecodes.BTN_TOUCH, 0)
        device.syn()
        time.sleep(args.hold_seconds)


def write_contact(
    device: UInput,
    x: int,
    y: int,
    tracking_id: int,
    down: bool,
) -> None:
    device.write(ecodes.EV_ABS, ecodes.ABS_MT_SLOT, 0)
    device.write(ecodes.EV_ABS, ecodes.ABS_MT_TRACKING_ID, tracking_id)
    device.write(ecodes.EV_ABS, ecodes.ABS_MT_POSITION_X, x)
    device.write(ecodes.EV_ABS, ecodes.ABS_MT_POSITION_Y, y)
    device.write(ecodes.EV_ABS, ecodes.ABS_X, x)
    device.write(ecodes.EV_ABS, ecodes.ABS_Y, y)
    device.write(ecodes.EV_KEY, ecodes.BTN_TOUCH, int(down))
    device.syn()


if __name__ == "__main__":
    main()
