"""Original geometric wordmark for "VELTRIX OS", drawn as stroked paths (no font dependency, fully reproducible).
Cap height 100 units, stroke 13 units, square terminals."""

STROKE = 13
H = 7  # half stroke
CAP = 100

# Each glyph: (advance width, centerline path). Coordinates already inset by half the stroke.
GLYPHS = {
    "V": (74, f"M{H} {H} L37 {CAP - H} L{74 - H} {H}"),
    "E": (60, f"M{60 - H} {H} H{H} V{CAP - H} H{60 - H} M{H} 50 H{52 - H}"),
    "L": (56, f"M{H} {H} V{CAP - H} H{56 - H}"),
    "T": (66, f"M0 {H} H66 M33 {H} V{CAP - H}"),
    "R": (64, f"M{H} {CAP} V{H} H38 A22 22 0 0 1 38 {50 + H} H{H} M34 {50 + H} L{64 - H} {CAP - H}"),
    "I": (14, f"M{H} {H} V{CAP - H}"),
    "X": (70, f"M{H} {H} L{70 - H} {CAP - H} M{70 - H} {H} L{H} {CAP - H}"),
    "O": (80, f"M40 {H} A{40 - H} {50 - H} 0 1 1 39.9 {H} Z"),
    "S": (62, f"M{62 - H} {H} H24 A{24 - H} {21.5 - H/2} 0 0 0 24 {50} H38 A{24 - H} {21.5 - H/2} 0 0 1 38 {CAP - H} H{H}"),
    "P": (60, f"M{H} {CAP} V{H} H36 A22 22 0 0 1 36 {50 + H} H{H}"),
    "W": (98, f"M{H} {H} L27 {CAP - H} L49 30 L71 {CAP - H} L{98 - H} {H}"),
    "N": (68, f"M{H} {CAP} V{H} L{68 - H} {CAP - H} V0"),
    "U": (68, f"M{H} 0 V62 A{34 - H} {34 - H} 0 0 0 {68 - H} 62 V0"),
    "C": (70, f"M{70 - H} {H + 4} H40 A{40 - H} {50 - H} 0 0 0 40 {CAP - H} H{70 - H}"),
    "G": (74, f"M{74 - H} {H + 4} H40 A{40 - H} {50 - H} 0 0 0 40 {CAP - H} H{74 - H} V52 H44"),
    "B": (62, f"M{H} {H} H36 A20 21 0 0 1 36 50 H{H} M{H} 50 H40 A21 {25 - H/2} 0 0 1 40 {CAP - H} H{H} Z"),
    " ": (34, ""),
}
TRACKING = 22


def layout(text):
    x = 0
    parts = []
    for i, ch in enumerate(text):
        w, d = GLYPHS[ch]
        if d:
            parts.append(f'<path transform="translate({x} 0)" d="{d}"/>')
        x += w + (TRACKING if i < len(text) - 1 else 0)
    return x, "".join(parts)


def wordmark_group(color, os_color=None, text="VELTRIX OS"):
    """Returns (width, svg group). 'OS' may use an accent color."""
    main, suffix = (text[:-3], text[-3:]) if text.endswith(" OS") else (text, "")
    w1, g1 = layout(main)
    out = f'<g fill="none" stroke="{color}" stroke-width="{STROKE}" stroke-linecap="square" stroke-linejoin="miter" stroke-miterlimit="2">{g1}</g>'
    width = w1
    if suffix:
        w2, g2 = layout(suffix)
        out += f'<g transform="translate({w1} 0)" fill="none" stroke="{os_color or color}" stroke-width="{STROKE}" stroke-linecap="square" stroke-linejoin="miter" stroke-miterlimit="2">{g2}</g>'
        width += w2
    return width, out
