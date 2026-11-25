"""Turn raw help screenshots into the images shipped in Assets/Help.

1. Build the test project, then capture the real UI in every language:
     DACT_TEST_CIMGUI=<Dalamud dev cimgui.dll>
     tests/DalamudActCompat.PackageSmokeTests/.../DalamudActCompat.PackageSmokeTests.exe --help-screenshots <raw>
2. python tools/build-help-images.py <raw> src/DalamudActCompat/Assets/Help

Requires Pillow. The captures keep their size and are only reduced to a 256-colour palette,
which takes the six languages from about 9 MB of RGB PNG to about 2 MB in the package.
"""
import os
import sys

from PIL import Image

IMAGES = [
    "account-login",
    "account-discord",
    "control-overview",
    "control-meter",
    "control-overlays",
    "control-extensions",
    "control-cloud",
    "control-diagnostics",
    "meter",
]
LANGUAGES = ["zh-CN", "zh-TW", "en", "ja", "de", "fr"]


def main(raw, output):
    total = 0
    for language in LANGUAGES:
        os.makedirs(os.path.join(output, language), exist_ok=True)
        for name in IMAGES:
            source = os.path.join(raw, language, name + ".png")
            # Nothing is cropped or scaled: the 1.0 control centre fills the whole capture,
            # and the crop this script had for the 0.4 window cut off its right and bottom.
            image = Image.open(source).convert("RGB")
            # 256 colours keep UI text crisp while cutting each file to a fraction of RGB PNG.
            # Maximum coverage, not median cut: median cut spends the palette on near-identical
            # shades of the flat panels, and the result compresses to 2.5 times the size.
            image = image.quantize(colors=256, method=Image.Quantize.MAXCOVERAGE, dither=Image.Dither.NONE)
            target = os.path.join(output, language, name + ".png")
            image.save(target, optimize=True)
            total += os.path.getsize(target)
    print(f"Wrote {len(LANGUAGES) * len(IMAGES)} images, {total / 1024:.0f} KiB total.")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit("usage: build-help-images.py <raw-capture-directory> <Assets/Help directory>")
    main(sys.argv[1], sys.argv[2])
