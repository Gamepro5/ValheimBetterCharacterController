#!/usr/bin/env python3
"""Sanity-check a built BetterCharacterController.dll before it is released.

Exists because of a real incident: a guard that grepped a built assembly for a string literal
in ASCII always failed, the failure skipped an install step, and a broken build shipped while
the logs looked fine. A check that cannot pass is worse than no check, because it gets worked
around instead of fixed.

The lesson is about metadata heaps. A .NET assembly keeps these in different places:

  identifiers (type, method and field names)   #Strings heap, UTF-8
  custom attribute arguments                   the attribute blob, UTF-8 length-prefixed
  IL string literals                           #US heap, UTF-16

So each thing below is looked for in the encoding it is actually stored in.

    python3 .github/verify-build.py BetterCharacterController.dll 1.6.0
"""
import sys

# Type names -> #Strings heap, UTF-8.
PATCH_CLASSES = (
    "MeleeAim",
    "AimLean",
    "Diving",
    "FirstPersonCamera",
    "LookSync",
    "EquipmentWatcher",
    "AchievementUnblock",
    "FastHoldInteract",
)

# IL string literals -> #US heap, UTF-16. These are the wire format: if a rename or a bad merge
# dropped one, remote players would silently stop leaning for everybody.
WIRE_KEYS = ("bcc_look_pitch", "bcc_look_yaw", "bcc_look_on")


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__)
        return 2
    dll, version = sys.argv[1], sys.argv[2]

    try:
        blob = open(dll, "rb").read()
    except OSError as e:
        print(f"error: cannot read {dll}: {e}")
        return 1

    problems = []

    # Reaches the file through [BepInPlugin(...)], whose arguments are UTF-8 in the attribute
    # blob - so this one genuinely is findable as plain bytes.
    if version.encode() not in blob:
        problems.append(f"the version {version} is not present (checked UTF-8, attribute blob)")

    for name in PATCH_CLASSES:
        if name.encode() not in blob:
            problems.append(f"patch class {name} is missing (checked UTF-8, #Strings)")

    for key in WIRE_KEYS:
        if key.encode("utf-16-le") not in blob:
            problems.append(f"wire key {key!r} is missing (checked UTF-16, #US)")

    if problems:
        print(f"{dll} failed verification:")
        for p in problems:
            print(f"  - {p}")
        return 1

    print(
        f"verified {dll}: version {version}, "
        f"{len(PATCH_CLASSES)} patch classes, {len(WIRE_KEYS)} wire keys"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
