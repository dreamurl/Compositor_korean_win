"""Reads the PSDs the Core tests exported with psd-tools, a reader written independently of ours.

Our own tests read every export back, but a reader agreeing with the writer it shares code with
proves little. psd-tools has read Photoshop's files for years; if it reads ours without complaint,
finds the layers, groups, adjustments and effects where we put them, and composites the layers to
the flattened image we saved beside them, Photoshop is very likely to read them too.

Usage: python scripts/check_psd.py build/psd-out
"""

import logging
import struct
import sys
import warnings
from pathlib import Path

import numpy as np
from psd_tools import PSDImage

failures = []


class Collect(logging.Handler):
    """psd-tools logs, rather than raises, when a block does not parse; those are failures here."""

    def __init__(self):
        super().__init__(level=logging.WARNING)
        self.records = []

    def emit(self, record):
        self.records.append(record.getMessage())


def walk(layers, depth=0):
    for layer in layers:
        yield depth, layer
        if layer.is_group():
            yield from walk(layer, depth + 1)


def check(path: Path):
    collect = Collect()
    logging.getLogger("psd_tools").addHandler(collect)
    try:
        with warnings.catch_warnings():
            warnings.simplefilter("error")
            psd = PSDImage.open(path)
            layers = list(walk(psd))
            for _, layer in layers:
                _ = layer.name, layer.kind, layer.visible, layer.opacity, layer.blend_mode, layer.bbox
                if layer.has_mask():
                    _ = layer.mask.bbox
                _ = list(layer.effects)
            saved = np.asarray(psd.topil().convert("RGBA"), dtype=np.float64)
    except Exception as exception:  # noqa: BLE001 - any failure to read is the finding
        failures.append(f"{path.name}: psd-tools could not read it: {exception!r}")
        return
    finally:
        logging.getLogger("psd_tools").removeHandler(collect)

    if collect.records:
        failures.append(f"{path.name}: psd-tools complained: {collect.records[:3]}")

    kinds = {}
    for _, layer in layers:
        kinds[layer.kind] = kinds.get(layer.kind, 0) + 1
    effects = sum(1 for _, layer in layers if len(list(layer.effects)) > 0)

    # Recompositing is compared only where psd-tools can draw everything in the file; it does not
    # draw every adjustment, and draws effects its own way.
    drawable = not any(layer.kind not in ("pixel", "group", "shape") for _, layer in layers) and effects == 0
    note = ""
    if drawable and layers:
        composed = np.asarray(psd.composite(ignore_preview=True).convert("RGBA"), dtype=np.float64)
        # Compare premultiplied, so colour under transparent pixels does not count.
        a = saved[..., :3] * saved[..., 3:] / 255
        b = composed[..., :3] * composed[..., 3:] / 255
        mean = float(np.abs(a - b).mean() + np.abs(saved[..., 3] - composed[..., 3]).mean())
        note = f", recomposited mean difference {mean:.2f}"
        if mean > 3:
            failures.append(f"{path.name}: psd-tools composites the layers {mean:.2f} away from the saved image")

    print(f"{path.name}: {psd.width}x{psd.height}, {len(layers)} layers {kinds}, {effects} with effects{note}")
    return psd, layers


def main(folder: str):
    files = sorted(Path(folder).glob("*.psd"))
    if not files:
        failures.append(f"no PSDs in {folder}: did the tests write them?")

    results = {path.name: check(path) for path in files}

    # The exporter's own document has one of everything; each must be where it was put.
    structure = results.get("structure.psd")
    if structure:
        _, layers = structure
        by_name = {layer.name: (depth, layer) for depth, layer in layers}
        expected = {
            "폴더": "group", "안": "pixel", "클립": "pixel", "레벨": "levels", "커브": "curves",
            "색조": "huesaturation", "노출": "exposure", "그레이디언트": "gradientmap",
        }
        for name, kind in expected.items():
            if name not in by_name:
                failures.append(f"structure.psd: no layer named {name}")
            elif by_name[name][1].kind != kind:
                failures.append(f"structure.psd: {name} is a {by_name[name][1].kind}, not a {kind}")
        if "안" in by_name and by_name["안"][0] != 1:
            failures.append("structure.psd: 안 is not inside 폴더")
        if "클립" in by_name and not by_name["클립"][1].clipping:
            failures.append("structure.psd: 클립 is not clipped")
        if "숨김" in by_name and by_name["숨김"][1].visible:
            failures.append("structure.psd: 숨김 is visible")
        if "효과" in by_name:
            found = sorted(effect.name for effect in by_name["효과"][1].effects)
            if found != ["DropShadow", "OuterGlow", "Stroke"]:
                failures.append(f"structure.psd: 효과 carries {found}")
        if "폴더" in by_name and not by_name["폴더"][1].has_mask():
            failures.append("structure.psd: 폴더 lost its mask")

    editable = results.get("editable-text.psd")
    if editable:
        psd, layers = editable
        type_layers = [layer for _, layer in layers if layer.kind == "type"]
        if len(type_layers) != 1:
            failures.append(f"editable-text.psd: expected one editable type layer, found {len(type_layers)}")
        elif type_layers[0].text.rstrip("\r") != "편집 가능한 글자":
            failures.append(f"editable-text.psd: type text was {type_layers[0].text!r}")
        else:
            # Adobe specifies four 8-byte bounds at the end of TySh. psd-tools models them as
            # four integers, so merely opening the file did not catch our former 16-byte tail.
            raw = (Path(folder) / "editable-text.psd").read_bytes()
            marker = raw.find(b"8BIMTySh")
            if marker < 0:
                failures.append("editable-text.psd: no TySh block")
            else:
                length = struct.unpack(">I", raw[marker + 8 : marker + 12])[0]
                block = raw[marker + 12 : marker + 12 + length]
                if len(block) != length or len(block) < 32 or block[-32:] != bytes(32):
                    failures.append("editable-text.psd: TySh does not end in four 8-byte bounds")

            setting = type_layers[0]._record.tagged_blocks.get_data(b"TySh")
            descriptor_text = setting.text_data[b"Txt "].value
            if descriptor_text != "편집 가능한 글자\x00":
                failures.append(f"editable-text.psd: descriptor text was {descriptor_text!r}")

    if failures:
        print("\n".join(["", "FAILED:"] + failures))
        sys.exit(1)
    print(f"\nAll {len(files)} exported PSDs read cleanly.")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "build/psd-out")
