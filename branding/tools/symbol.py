"""VELTRIX symbol geometry (single source of truth for every brand asset).

The mark is an original geometric "V" built from two precision blades that do not touch:
a wide left blade (electric blue -> cyan) and a narrower right blade (violet -> electric blue).
A circuit trace with two nodes runs along the inside of the left blade, and a cyan core
node sits in the gap where the blades converge — suggesting connectivity and intelligence.
All coordinates are on a 512 x 512 grid.
"""

BLUE = "#3478FF"
CYAN = "#00D9FF"
VIOLET = "#765BFF"
NAVY = "#07111F"
NAVY2 = "#0D1D33"
WHITE = "#F5F8FF"

# Left blade: from top-left down to the vertex.
LEFT = [(84, 104), (184, 104), (282, 356), (240, 432)]
# Right blade: from top-right down, stopping short of the vertex (precision gap).
RIGHT = [(428, 104), (346, 104), (282, 268), (322, 336)]
# Circuit trace along the inner edge of the left blade.
TRACE = [(156, 156), (226, 316)]
NODE_R = 9
CORE = (300, 384)
CORE_R = 14


def poly(points):
    return "M " + " L ".join(f"{x} {y}" for x, y in points) + " Z"


def defs(uid="v"):
    return f"""<defs>
    <linearGradient id="{uid}L" x1="84" y1="104" x2="260" y2="432" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="{BLUE}"/><stop offset="1" stop-color="{CYAN}"/>
    </linearGradient>
    <linearGradient id="{uid}R" x1="428" y1="104" x2="300" y2="336" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="{VIOLET}"/><stop offset="1" stop-color="{BLUE}"/>
    </linearGradient>
  </defs>"""


def symbol_group(mode="color", uid="v"):
    """mode: color | mono-light (white on dark) | mono-dark (navy on light)."""
    if mode == "color":
        lf, rf, tr, core = f"url(#{uid}L)", f"url(#{uid}R)", WHITE, CYAN
        trace_op = "0.85"
    elif mode == "mono-light":
        lf = rf = tr = core = WHITE
        trace_op = "1"
    else:
        lf = rf = core = NAVY
        tr = "#FFFFFF"
        trace_op = "1"
    (x1, y1), (x2, y2) = TRACE
    return f"""<g>
    <path d="{poly(LEFT)}" fill="{lf}"/>
    <path d="{poly(RIGHT)}" fill="{rf}"/>
    <path d="M {x1} {y1} L {x2} {y2}" stroke="{tr}" stroke-opacity="{trace_op}" stroke-width="6" stroke-linecap="round" fill="none"/>
    <circle cx="{x1}" cy="{y1}" r="{NODE_R}" fill="{tr}" fill-opacity="{trace_op}"/>
    <circle cx="{x2}" cy="{y2}" r="{NODE_R}" fill="{tr}" fill-opacity="{trace_op}"/>
    <circle cx="{CORE[0]}" cy="{CORE[1]}" r="{CORE_R}" fill="{core}"/>
  </g>"""


def symbol_svg(mode="color", background=None, size=512, padding=0, radius=0):
    bg = ""
    if background:
        bg = f'<rect x="0" y="0" width="512" height="512" rx="{radius}" fill="{background}"/>'
    scale = (512 - 2 * padding) / 512
    return f"""<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 512 512" role="img" aria-label="VELTRIX">
  {defs()}
  {bg}
  <g transform="translate({padding} {padding}) scale({scale})">{symbol_group(mode)}</g>
</svg>
"""
