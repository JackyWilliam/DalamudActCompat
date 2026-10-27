"""Turn raw help screenshots into the images shipped in Assets/Help.

1. Build Release, then capture the real UI in every language:
     DACT_TEST_CIMGUI=<Dalamud dev cimgui.dll>
     tests/DalamudActCompat.PackageSmokeTests/.../DalamudActCompat.PackageSmokeTests.exe --help-screenshots <raw>
2. python tools/build-help-images.py <raw> src/DalamudActCompat/Assets/Help

Requires Pillow. The output is palette PNG so the six languages stay small in the package.
"""
import os
import sys

from PIL import Image

# (crop box in the raw capture, output width). Captures draw the window at (10, 10).
CONTROL_CENTER = ((10, 10, 890, 630), 720)
IMAGES = {
    "account-login": CONTROL_CENTER,
    "account-discord": CONTROL_CENTER,
    "control-overview": CONTROL_CENTER,
    "control-meter": CONTROL_CENTER,
    "control-overlays": CONTROL_CENTER,
    "control-extensions": CONTROL_CENTER,
    "control-cloud": CONTROL_CENTER,
    "control-diagnostics": CONTROL_CENTER,
    "meter": ((10, 10, 550, 310), 540),
}
LANGUAGES = ["zh-CN", "zh-TW", "en", "ja", "de", "fr"]


def main(raw, output):
    total = 0
    for language in LANGUAGES:
        os.makedirs(os.path.join(output, language), exist_ok=True)
        for name, (box, width) in IMAGES.items():
            source = os.path.join(raw, language, name + ".png")
            image = Image.open(source).convert("RGB").crop(box)
            if image.width != width:
                image = image.resize((width, round(image.height * width / image.width)), Image.LANCZOS)
            # 256 colours keep UI text crisp while cutting each file to a fraction of RGBA PNG.
            image = image.quantize(colors=256, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
            target = os.path.join(output, language, name + ".png")
            image.save(target, optimize=True)
            total += os.path.getsize(target)
    print(f"Wrote {len(LANGUAGES) * len(IMAGES)} images, {total / 1024:.0f} KiB total.")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit("usage: build-help-images.py <raw-capture-directory> <Assets/Help directory>")
    main(sys.argv[1], sys.argv[2])
