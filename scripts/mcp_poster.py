"""Builds a poster through the editor's MCP server, the way a model would, and checks every step.

It starts the published program with --mcp, speaks JSON-RPC to it over standard input and output,
and uses nothing but the tools a model sees: new_document, add_text with warp, add_shape, gradients
clipped to shapes and type, masks, effects, adjustments, a photograph with its background removed,
render to look, and save_document / export_image for the results.

The poster follows the one the MCP work was planned against (docs/progress.md 13.1): black ground,
BRUTALISM fading out, a yellow-to-green panel, a black-and-white figure in front of it, EXHIBITION
mirrored below, "Choose Your Style" arched, FREE ENTRY running up the side, four-point stars and thin
lines.

The figure comes from generate_image. The server is started with COMPOSITOR_IMAGE_COMMAND pointing at
scripts/stand_in_image.py, which draws a plain bust, so the whole generator path runs without an image
service account — and without a downloaded photograph, whose content nobody reviews before the poster
is published to the mcp-results branch.

Usage: python scripts/mcp_poster.py <path to Compositor_korean_win.exe> <output folder>
"""

import base64
import json
import os
import subprocess
import sys
import time

STAND_IN = os.path.join(os.path.dirname(os.path.abspath(__file__)), "stand_in_image.py")


class Client:
    def __init__(self, exe):
        environment = dict(os.environ)
        environment["COMPOSITOR_IMAGE_GENERATOR"] = "command"
        environment["COMPOSITOR_IMAGE_COMMAND"] = f'"{sys.executable}" "{STAND_IN}" "{{output}}" {{width}} {{height}}'
        self.process = subprocess.Popen(
            [exe, "--mcp"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=sys.stderr,
            bufsize=0, env=environment)
        self.next_id = 0
        self.calls = 0

    def request(self, method, params=None):
        self.next_id += 1
        message = {"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params or {}}
        self.process.stdin.write((json.dumps(message, ensure_ascii=False) + "\n").encode("utf-8"))
        self.process.stdin.flush()
        line = self.process.stdout.readline()
        if not line:
            raise SystemExit(f"the server closed while answering {method}")
        reply = json.loads(line.decode("utf-8"))
        if "error" in reply:
            raise SystemExit(f"{method} failed: {reply['error']}")
        return reply["result"]

    def call(self, tool, **arguments):
        started = time.perf_counter()
        result = self.request("tools/call", {"name": tool, "arguments": arguments})
        self.calls += 1
        texts = [part["text"] for part in result["content"] if part["type"] == "text"]
        took = (time.perf_counter() - started) * 1000
        summary = texts[0].splitlines()[0] if texts else ""
        print(f"  {tool:<16} {took:7.0f} ms  {summary[:110]}")
        if result.get("isError"):
            raise SystemExit(f"{tool} failed: {' '.join(texts)}")
        return result

    @staticmethod
    def layer_id(result):
        for part in result["content"]:
            if part["type"] != "text":
                continue
            for word in part["text"].replace("'", " ").replace(",", " ").split():
                word = word.rstrip(".")
                if len(word) == 36 and word.count("-") == 4:
                    return word
        raise SystemExit("no layer id in " + json.dumps(result)[:300])

    def close(self):
        self.process.stdin.close()
        self.process.wait(timeout=30)


def main(exe, folder):
    os.makedirs(folder, exist_ok=True)
    client = Client(exe)

    info = client.request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                         "clientInfo": {"name": "poster-check", "version": "1"}})
    print(f"server {info['serverInfo']['name']} {info['serverInfo']['version']}, protocol {info['protocolVersion']}")
    tools = client.request("tools/list")["tools"]
    print(f"{len(tools)} tools: {', '.join(tool['name'] for tool in tools)}")

    fonts = client.call("list_fonts", query="Arial")
    families = fonts["content"][0]["text"].splitlines()
    heavy = "Arial Black" if "Arial Black" in families else "Arial"

    c = client.call
    c("new_document", width=1080, height=1350, background="#0B0B0B", name="brutalism poster")

    # Thin rules framing the page.
    c("add_shape", kind="line", x1=48, y1=56, x2=1032, y2=56, color="#FFFFFF", line_width=2, name="rule top")
    c("add_shape", kind="line", x1=48, y1=1300, x2=1032, y2=1300, color="#FFFFFF", line_width=2, name="rule bottom")
    c("add_shape", kind="line", x1=70, y1=1270, x2=330, y2=1060, color="#FFFFFF", line_width=2, name="diagonal")

    # BRUTALISM: heavy type, coloured by a gradient clipped to it, fading towards the panel.
    title = client.layer_id(c("add_text", text="BRUTALISM", x=540, y=250, font=heavy, size=176, weight=900,
                              align="center", tracking=-30, color="#FFFFFF", name="BRUTALISM"))
    title_colour = client.layer_id(c("add_gradient", start={"x": 0, "y": 90}, end={"x": 0, "y": 260},
                                     **{"from": "#FFFFFF", "to": "#F2E500"}, name="title colour"))
    c("set_clipping", layer=title_colour)
    c("set_mask", layer=title, shape="linear_gradient", start={"x": 0, "y": 150}, end={"x": 0, "y": 330})

    # The panel: a white rectangle coloured yellow to green by a clipped gradient, with a shadow.
    panel = client.layer_id(c("add_shape", kind="rectangle", x=190, y=330, width=700, height=660, fill="#FFFFFF", name="panel"))
    panel_colour = client.layer_id(c("add_gradient", start={"x": 190, "y": 330}, end={"x": 890, "y": 990},
                                     **{"from": "#F2E500", "to": "#16B364"}, name="panel colour"))
    c("set_clipping", layer=panel_colour)
    c("set_effects", layer=panel, drop_shadow={"distance": 24, "size": 48, "opacity": 0.55, "angle": 120})

    # The figure: generated, its background removed, made black and white, in front of the panel.
    figure = client.layer_id(c("generate_image", prompt="Studio portrait of a person in a dark suit, head and shoulders, "
                               "plain light grey backdrop, soft light from the upper left", width=620, height=760,
                               x=230, y=300, save_to=os.path.abspath(os.path.join(folder, "figure.png")), name="figure"))
    try:
        c("remove_background", layer=figure)
    except SystemExit as reason:
        print(f"  (background removal skipped: {reason})")
    c("add_adjustment", kind="hue_saturation", saturation=-100, above=figure, clip=True, name="black and white")
    c("add_adjustment", kind="levels", black=20, white=225, above="black and white", clip=True, name="contrast")

    # EXHIBITION mirrored below the panel, fading out as a reflection does.
    exhibition = client.layer_id(c("add_text", text="EXHIBITION", x=540, y=1150, font=heavy, size=118, weight=900,
                                   align="center", tracking=40, color="#FFFFFF", name="EXHIBITION"))
    c("update_layer", layer=exhibition, flip_y=True)
    c("set_mask", layer=exhibition, shape="linear_gradient", start={"x": 0, "y": 1060}, end={"x": 0, "y": 1180})

    # Choose Your Style, arched over the panel's foot.
    c("add_text", text="Choose Your Style", x=540, y=1040, font="Georgia", size=58, italic=True, align="center",
      color="#F2E500", warp={"style": "arc", "bend": 35}, name="Choose Your Style")

    # FREE ENTRY running up the right edge.
    c("add_text", text="FREE ENTRY", x=1000, y=700, font="Arial", size=34, weight=700, align="center",
      tracking=420, color="#FFFFFF", rotation=-90, name="FREE ENTRY")

    # Four-point stars: a filled sparkle, an outlined one, a small one.
    c("add_shape", kind="star", x=850, y=110, width=130, height=130, sides=4, inset=0.14, curved=True, fill="#FFFFFF", name="sparkle")
    c("add_shape", kind="star", x=96, y=980, width=80, height=80, sides=4, inset=0.22, fill="none", stroke="#FFFFFF",
      stroke_width=3, name="star outline")
    c("add_shape", kind="star", x=950, y=1180, width=44, height=44, sides=4, inset=0.18, curved=True, fill="#F2E500", name="small star")

    # Small print, in Korean too.
    c("add_text", text="2026.10.01 — 11.30   SEOUL", x=60, y=1285, font="Arial", size=24, color="#FFFFFF", tracking=120, name="dates")
    c("add_text", text="브루탈리즘 전시", x=1020, y=1285, font="Malgun Gothic", size=26, weight=700, align="right",
      color="#FFFFFF", name="korean")

    described = c("get_document")["content"][0]["text"]
    look = c("render", max_size=1350, background="black")
    png = next(part for part in look["content"] if part["type"] == "image")
    with open(os.path.join(folder, "poster-render.png"), "wb") as file:
        file.write(base64.b64decode(png["data"]))

    c("save_document", path=os.path.abspath(os.path.join(folder, "poster.psd")))
    c("save_document", path=os.path.abspath(os.path.join(folder, "poster.comp")))
    c("export_image", path=os.path.abspath(os.path.join(folder, "poster.png")))
    c("export_image", path=os.path.abspath(os.path.join(folder, "poster.jpg")), quality=88)

    # The saved project opens again with the same layers.
    layers = json.loads(described[described.index("{"):])["layers"]
    reopened = c("open_document", path=os.path.abspath(os.path.join(folder, "poster.comp")))
    text = reopened["content"][0]["text"]
    again = json.loads(text[text.index("{"):])["layers"]
    if len(again) != len(layers):
        raise SystemExit(f"{len(layers)} layers went into poster.comp and {len(again)} came back")

    with open(os.path.join(folder, "layers.json"), "w", encoding="utf-8") as file:
        file.write(described[described.index("{"):])

    client.close()
    print(f"\n{client.calls} tool calls; {len(layers)} layers; results in {folder}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else "build/mcp-poster")
