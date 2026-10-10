#!/usr/bin/env python3
"""Generates the standalone brand intro (HTML/SVG/CSS) and a Lottie (Bodymovin 5.7) representation."""
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import symbol as S  # noqa: E402
import wordmark as W  # noqa: E402

OUT = os.path.join(HERE, "..", "motion")


def html():
    w, g = W.wordmark_group(S.WHITE, S.CYAN)
    (x1, y1), (x2, y2) = S.TRACE
    return f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>VELTRIX OS — Brand intro</title>
<style>
  html,body{{margin:0;height:100%;background:{S.NAVY};display:grid;place-items:center;overflow:hidden}}
  svg{{width:min(80vw,640px);max-height:90vh;height:auto}}
  .bg{{animation:fade .45s ease-out both}}
  .left{{animation:rise .9s cubic-bezier(.2,.8,.2,1) .3s both}}
  .right{{animation:rise .9s cubic-bezier(.2,.8,.2,1) .45s both}}
  .trace{{stroke-dasharray:180;stroke-dashoffset:180;animation:draw .7s ease-out .8s forwards}}
  .node{{transform-box:fill-box;transform-origin:center;animation:pop .35s ease-out 1.1s both}}
  .core{{transform-box:fill-box;transform-origin:center;animation:pop .4s ease-out 1.3s both, glow 1.2s ease-in-out 1.4s 1 both}}
  .word{{animation:rise .7s ease-out 1.5s both}}
  .p{{animation:drift 6s linear infinite}}
  @keyframes fade{{from{{opacity:0}}to{{opacity:1}}}}
  @keyframes rise{{from{{opacity:0;transform:translateY(24px)}}to{{opacity:1;transform:none}}}}
  @keyframes draw{{to{{stroke-dashoffset:0}}}}
  @keyframes pop{{from{{opacity:0;transform:scale(.6)}}to{{opacity:1;transform:scale(1)}}}}
  @keyframes glow{{0%{{filter:drop-shadow(0 0 0 {S.CYAN})}}50%{{filter:drop-shadow(0 0 12px {S.CYAN})}}100%{{filter:drop-shadow(0 0 6px {S.CYAN})}}}}
  @keyframes drift{{from{{transform:translateY(0)}}to{{transform:translateY(-60px)}}}}
  @media (prefers-reduced-motion: reduce){{*{{animation:none!important;opacity:1!important;stroke-dashoffset:0!important}}}}
</style></head><body>
<svg viewBox="0 0 {max(512, w * 0.6):.0f} 700" role="img" aria-label="VELTRIX OS">
  {S.defs()}
  <g class="bg">{''.join(f'<circle class="p" cx="{(i * 97) % 600}" cy="{120 + (i * 53) % 520}" r="2" fill="{S.CYAN}" opacity="0.3"/>' for i in range(14))}</g>
  <g transform="translate({(max(512, w * 0.6) - 512) / 2} 0)">
    <path class="left" d="{S.poly(S.LEFT)}" fill="url(#vL)"/>
    <path class="right" d="{S.poly(S.RIGHT)}" fill="url(#vR)"/>
    <path class="trace" d="M {x1} {y1} L {x2} {y2}" stroke="{S.WHITE}" stroke-opacity=".85" stroke-width="6" stroke-linecap="round"/>
    <circle class="node" cx="{x1}" cy="{y1}" r="{S.NODE_R}" fill="{S.WHITE}"/>
    <circle class="node" cx="{x2}" cy="{y2}" r="{S.NODE_R}" fill="{S.WHITE}"/>
    <circle class="core" cx="{S.CORE[0]}" cy="{S.CORE[1]}" r="{S.CORE_R}" fill="{S.CYAN}"/>
  </g>
  <g transform="translate({(max(512, w * 0.6) - w * 0.6) / 2} 560) scale(0.6)"><g class="word">{g}</g></g>
</svg></body></html>
"""


def lottie():
    fr = 60
    def shape_path(points):
        v = [[x - 256, y - 256] for x, y in points]
        return {"ty": "sh", "ks": {"a": 0, "k": {"i": [[0, 0]] * len(v), "o": [[0, 0]] * len(v), "v": v, "c": True}}}
    def fill(hex_color, a=1):
        r, g, b = (int(hex_color[i:i + 2], 16) / 255 for i in (1, 3, 5))
        return {"ty": "fl", "c": {"a": 0, "k": [r, g, b, 1]}, "o": {"a": 0, "k": 100 * a}, "r": 1}
    def layer(ind, name, shapes, t_in, dy=24):
        return {
            "ddd": 0, "ind": ind, "ty": 4, "nm": name, "sr": 1, "ip": 0, "op": 180, "st": 0, "bm": 0,
            "ks": {
                "o": {"a": 1, "k": [{"t": t_in, "s": [0], "e": [100], "i": {"x": [0.2], "y": [1]}, "o": {"x": [0.2], "y": [0.8]}}, {"t": t_in + 50}]},
                "p": {"a": 1, "k": [{"t": t_in, "s": [256, 256 + dy, 0], "e": [256, 256, 0], "i": {"x": 0.2, "y": 1}, "o": {"x": 0.2, "y": 0.8}}, {"t": t_in + 50}]},
                "a": {"a": 0, "k": [0, 0, 0]}, "s": {"a": 0, "k": [100, 100, 100]}, "r": {"a": 0, "k": 0},
            },
            "shapes": [{"ty": "gr", "it": shapes + [{"ty": "tr", "p": {"a": 0, "k": [0, 0]}, "a": {"a": 0, "k": [0, 0]}, "s": {"a": 0, "k": [100, 100]}, "r": {"a": 0, "k": 0}, "o": {"a": 0, "k": 100}}]}],
        }
    cx, cy = S.CORE
    core = [{"ty": "el", "p": {"a": 0, "k": [cx - 256, cy - 256]}, "s": {"a": 0, "k": [2 * S.CORE_R, 2 * S.CORE_R]}}, fill(S.CYAN)]
    bg = {"ddd": 0, "ind": 99, "ty": 1, "nm": "background", "sc": S.NAVY, "sw": 512, "sh": 512, "ip": 0, "op": 180, "st": 0,
          "ks": {"o": {"a": 1, "k": [{"t": 0, "s": [0], "e": [100]}, {"t": 27}]}, "p": {"a": 0, "k": [256, 256, 0]}, "a": {"a": 0, "k": [256, 256, 0]},
                 "s": {"a": 0, "k": [100, 100, 100]}, "r": {"a": 0, "k": 0}}}
    return {
        "v": "5.7.4", "fr": fr, "ip": 0, "op": 180, "w": 512, "h": 512, "nm": "VELTRIX OS intro", "ddd": 0, "assets": [],
        "layers": [
            layer(1, "core", core, 78, dy=0),
            layer(2, "right-blade", [shape_path(S.RIGHT), fill(S.VIOLET)], 27),
            layer(3, "left-blade", [shape_path(S.LEFT), fill(S.BLUE)], 18),
            bg,
        ],
    }


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, "veltrix-intro.html"), "w", encoding="utf-8") as f:
        f.write(html())
    with open(os.path.join(OUT, "veltrix-intro.lottie.json"), "w", encoding="utf-8") as f:
        json.dump(lottie(), f, separators=(",", ":"))
    print("Motion exports generated.")
