"""
Generates the font and image fixtures the writer tests decode.

Everything here is synthetic and made by this script, so the fixtures carry no third-party
licence and the tests do not depend on whatever fonts happen to be installed. Rerun with:

    python -I Theoistic.PDF.Tests/Fixtures/generate.py

Needs fontTools and Pillow. The PNGs are written by a deliberately small encoder in this file
rather than by Pillow, because Pillow cannot produce every colour type, bit depth and filter
combination, nor interlaced output, and those are exactly the cases worth testing. Each PNG
gets an .rgba file beside it: the expected pixels, computed from the same source values, which
is the oracle the decoder is compared against.
"""
import os
import struct
import zlib

from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTCollection, TTFont
from fontTools.ttLib.tables._g_l_y_f import Glyph, GlyphComponent
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
FONTS = os.path.join(HERE, "Fonts")
IMAGES = os.path.join(HERE, "Images")


# ------------------------------------------------------------------------------------ fonts

def rect(x0, y0, x1, y1):
    pen = TTGlyphPen(None)
    pen.moveTo((x0, y0))
    pen.lineTo((x0, y1))
    pen.lineTo((x1, y1))
    pen.lineTo((x1, y0))
    pen.closePath()
    return pen.glyph()


def composite(*parts):
    glyph = Glyph()
    glyph.numberOfContours = -1
    glyph.components = []
    for name, dx, dy, scale in parts:
        component = GlyphComponent()
        component.glyphName = name
        component.x = dx
        component.y = dy
        component.flags = 0
        if scale is not None:
            # A uniform scale sets WE_HAVE_A_SCALE, which shifts every following field in the
            # component record - the case a component walker most easily gets wrong.
            component.transform = [[scale, 0], [0, scale]]
        glyph.components.append(component)
    return glyph


def build_font(path, family, fs_type=0, supplementary=True, bold=False):
    order = [".notdef", "space", "A", "B", "L", "a", "stroke", "acute", "Lslash", "aacute"]
    cmap = {0x20: "space", 0x41: "A", 0x42: "B", 0x4C: "L", 0x61: "a", 0x141: "Lslash", 0xE1: "aacute"}

    if supplementary:
        # Beyond the BMP, so the font gets a format 12 subtable as well as format 4.
        order.append("smile")
        cmap[0x1F600] = "smile"

    weight = 120 if bold else 80
    glyphs = {
        ".notdef": rect(50, 0, 450, 700),
        "space": TTGlyphPen(None).glyph(),
        "A": rect(40, 0, 560, 700),
        "B": rect(60, 0, 540, 700),
        "L": rect(60, 0, 60 + weight, 700),
        "a": rect(40, 0, 460, 500),
        "stroke": rect(0, 300, 260, 300 + weight // 2),
        "acute": rect(150, 560, 250, 720),
        "Lslash": composite(("L", 0, 0, None), ("stroke", 20, 0, None)),
        "aacute": composite(("a", 0, 0, None), ("acute", 40, 20, 0.75)),
        "smile": rect(100, 0, 900, 800),
    }

    widths = {
        ".notdef": 500, "space": 250, "A": 600, "B": 580, "L": 520, "a": 500,
        "stroke": 300, "acute": 400, "Lslash": 520, "aacute": 500, "smile": 1000,
    }

    builder = FontBuilder(1000, isTTF=True)
    builder.setupGlyphOrder(order)
    builder.setupCharacterMap(cmap)
    builder.setupGlyf({name: glyphs[name] for name in order})
    builder.setupHorizontalMetrics({name: (widths[name], 0) for name in order})
    builder.setupHorizontalHeader(ascent=800, descent=-200)
    builder.setupNameTable({
        "familyName": family,
        "styleName": "Bold" if bold else "Regular",
        "psName": family.replace(" ", "") + ("-Bold" if bold else "-Regular"),
    })
    builder.setupOS2(
        version=4,
        sTypoAscender=800,
        sTypoDescender=-200,
        usWinAscent=800,
        usWinDescent=200,
        sCapHeight=700,
        fsType=fs_type,
        usWeightClass=700 if bold else 400,
        fsSelection=0x20 if bold else 0x40,
    )
    builder.setupPost()

    # The head table carries its own bold flag, and the loader reads it alongside OS/2's.
    builder.font["head"].macStyle = 0x01 if bold else 0x00
    builder.save(path)


def build_wide_font(path, bold=False):
    """
    Printable ASCII, the no-break space and middle dot, and the Polish letters: enough to set a
    whole invoice for a Polish buyer, which the tests use to prove a TrueType family lifts the
    WinAnsi limit. Every glyph is a plain box; only coverage and widths matter.
    """
    polish = "ĄąĆćĘęŁłŃńÓóŚśŹźŻż"
    codes = list(range(0x20, 0x7F)) + [0xA0, 0xB7] + [ord(c) for c in polish]

    order = [".notdef"] + [f"uni{code:04X}" for code in codes]
    cmap = {code: f"uni{code:04X}" for code in codes}
    weight = 90 if bold else 60

    glyphs = {".notdef": rect(50, 0, 450, 700)}
    widths = {".notdef": 500}

    for code in codes:
        name = f"uni{code:04X}"
        if code in (0x20, 0xA0):
            glyphs[name] = TTGlyphPen(None).glyph()
            widths[name] = 250
        else:
            glyphs[name] = rect(40, 0, 40 + weight + (code % 5) * 40, 700)
            widths[name] = 500 + (code % 3) * 50

    builder = FontBuilder(1000, isTTF=True)
    builder.setupGlyphOrder(order)
    builder.setupCharacterMap(cmap)
    builder.setupGlyf(glyphs)
    builder.setupHorizontalMetrics({name: (widths[name], 0) for name in order})
    builder.setupHorizontalHeader(ascent=800, descent=-200)
    style = "Bold" if bold else "Regular"
    builder.setupNameTable({"familyName": "Theoistic Test Wide", "styleName": style, "psName": "TheoisticTestWide-" + style})
    builder.setupOS2(version=4, sTypoAscender=800, sTypoDescender=-200, usWinAscent=800, usWinDescent=200,
                     sCapHeight=700, usWeightClass=700 if bold else 400, fsSelection=0x20 if bold else 0x40)
    builder.setupPost()
    builder.font["head"].macStyle = 0x01 if bold else 0x00
    builder.save(path)


def fonts():
    os.makedirs(FONTS, exist_ok=True)

    build_wide_font(os.path.join(FONTS, "TestWide.ttf"))
    build_wide_font(os.path.join(FONTS, "TestWide-Bold.ttf"), bold=True)

    build_font(os.path.join(FONTS, "TestSans.ttf"), "Theoistic Test Sans")
    build_font(os.path.join(FONTS, "TestSans-Bold.ttf"), "Theoistic Test Sans", bold=True)
    build_font(os.path.join(FONTS, "TestSansBmp.ttf"), "Theoistic Test Bmp", supplementary=False)
    build_font(os.path.join(FONTS, "TestRestricted.ttf"), "Theoistic Test Restricted", fs_type=0x0002)
    build_font(os.path.join(FONTS, "TestNoSubset.ttf"), "Theoistic Test NoSubset", fs_type=0x0100)

    collection = TTCollection()
    collection.fonts = [
        TTFont(os.path.join(FONTS, "TestSans.ttf")),
        TTFont(os.path.join(FONTS, "TestSans-Bold.ttf")),
    ]
    collection.save(os.path.join(FONTS, "TestCollection.ttc"))

    # Only the header matters: the loader rejects CFF outlines on the first four bytes.
    with open(os.path.join(FONTS, "TestCff.otf"), "wb") as f:
        f.write(b"OTTO" + bytes(60))


# ------------------------------------------------------------------------------------ PNG

WIDTH, HEIGHT = 13, 7          # odd sizes: partial bytes at low bit depths, ragged Adam7 passes
CHANNELS = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}
ADAM7 = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]

PALETTE = [
    (255, 0, 0), (0, 160, 0), (0, 0, 255), (250, 200, 0),
    (0, 0, 0), (255, 255, 255), (128, 0, 128), (0, 128, 128),
    (31, 58, 95), (200, 100, 50), (90, 90, 90), (10, 220, 180),
    (240, 128, 128), (60, 60, 200), (180, 180, 0), (1, 2, 3),
]


def chunk(kind, data):
    body = kind + data
    return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)


def paeth(a, b, c):
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    return a if pa <= pb and pa <= pc else (b if pb <= pc else c)


def pack(samples, depth):
    if depth == 8:
        return bytes(samples)
    if depth == 16:
        return b"".join(struct.pack(">H", s) for s in samples)
    per_byte = 8 // depth
    out = bytearray()
    for i in range(0, len(samples), per_byte):
        group = samples[i:i + per_byte]
        value = 0
        for s in group:
            value = (value << depth) | s
        value <<= depth * (per_byte - len(group))
        out.append(value)
    return bytes(out)


def filtered(rows, bytes_per_pixel):
    """Filters rows cycling through all five filter types, so every unfilter path is exercised."""
    out = bytearray()
    previous = bytes(len(rows[0])) if rows else b""
    for index, row in enumerate(rows):
        kind = index % 5
        out.append(kind)
        for j, x in enumerate(row):
            a = row[j - bytes_per_pixel] if j >= bytes_per_pixel else 0
            b = previous[j]
            c = previous[j - bytes_per_pixel] if j >= bytes_per_pixel else 0
            predictor = (0, a, b, (a + b) // 2, paeth(a, b, c))[kind]
            out.append((x - predictor) & 0xFF)
        previous = row
    return bytes(out)


def encode_png(path, width, height, color_type, depth, pixel, palette=None, trns=None, interlace=False):
    channels = CHANNELS[color_type]
    bytes_per_pixel = max(1, channels * depth // 8)

    def rows_for(xs, ys):
        return [pack([s for x in xs for s in pixel(x, y)], depth) for y in ys]

    if interlace:
        raw = b""
        for sx, sy, dx, dy in ADAM7:
            xs = list(range(sx, width, dx))
            ys = list(range(sy, height, dy))
            if xs and ys:
                raw += filtered(rows_for(xs, ys), bytes_per_pixel)
    else:
        raw = filtered(rows_for(range(width), range(height)), bytes_per_pixel)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, depth, color_type, 0, 0, 1 if interlace else 0))
    if palette is not None:
        png += chunk(b"PLTE", bytes(c for rgb in palette for c in rgb))
    if trns is not None:
        png += chunk(b"tRNS", trns)
    png += chunk(b"IDAT", zlib.compress(raw, 9))
    png += chunk(b"IEND", b"")

    with open(path, "wb") as f:
        f.write(png)


def scale(value, depth):
    maximum = (1 << depth) - 1
    return value if maximum == 255 else (value * 255 + maximum // 2) // maximum


def write_expected(path, width, height, rgba):
    with open(path, "wb") as f:
        for y in range(height):
            for x in range(width):
                f.write(bytes(rgba(x, y)))


def value(x, y, seed, depth):
    return (x * 7919 + y * 104729 + seed * 1299709) % (1 << depth)


def images():
    os.makedirs(IMAGES, exist_ok=True)
    w, h = WIDTH, HEIGHT

    def emit(name, color_type, depth, pixel, rgba, palette=None, trns=None, interlace=False, size=(w, h)):
        encode_png(os.path.join(IMAGES, name + ".png"), size[0], size[1], color_type, depth, pixel, palette, trns, interlace)
        write_expected(os.path.join(IMAGES, name + ".rgba"), size[0], size[1], rgba)

        # Pillow opening and fully loading each file is an independent check that the encoder
        # above writes valid PNGs, so the oracle is not just agreeing with itself.
        with Image.open(os.path.join(IMAGES, name + ".png")) as check:
            check.load()

    for depth in (1, 2, 4, 8, 16):
        gray = lambda x, y, d=depth: (value(x, y, 1, d),)
        rgba = lambda x, y, d=depth: (scale(value(x, y, 1, d), d),) * 3 + (255,)
        emit(f"gray{depth}", 0, depth, gray, rgba)
        emit(f"gray{depth}_interlaced", 0, depth, gray, rgba, interlace=True)

    # Grey with a transparent sample value, at native depth.
    target = value(3, 2, 1, 4)
    emit("gray4_trns", 0, 4,
         lambda x, y: (value(x, y, 1, 4),),
         lambda x, y: (scale(value(x, y, 1, 4), 4),) * 3 + (0 if value(x, y, 1, 4) == target else 255,),
         trns=struct.pack(">H", target))

    for depth in (8, 16):
        rgb = lambda x, y, d=depth: (value(x, y, 1, d), value(x, y, 2, d), value(x, y, 3, d))
        rgba = lambda x, y, d=depth: tuple(scale(v, d) for v in rgb(x, y)) + (255,)
        emit(f"rgb{depth}", 2, depth, rgb, rgba)
        emit(f"rgb{depth}_interlaced", 2, depth, rgb, rgba, interlace=True)

    key = (value(5, 1, 1, 8), value(5, 1, 2, 8), value(5, 1, 3, 8))
    rgb8 = lambda x, y: (value(x, y, 1, 8), value(x, y, 2, 8), value(x, y, 3, 8))
    emit("rgb8_trns", 2, 8, rgb8,
         lambda x, y: rgb8(x, y) + (0 if rgb8(x, y) == key else 255,),
         trns=struct.pack(">HHH", *key))

    for depth in (1, 2, 4, 8):
        entries = min(len(PALETTE), 1 << depth)
        index = lambda x, y, n=entries: ((x + y * 3) % n,)
        rgba = lambda x, y, n=entries: PALETTE[(x + y * 3) % n] + (255,)
        emit(f"palette{depth}", 3, depth, index, rgba, palette=PALETTE[:entries])
        emit(f"palette{depth}_interlaced", 3, depth, index, rgba, palette=PALETTE[:entries], interlace=True)

    # Alpha for only the first few entries: the rest must come out opaque.
    alphas = [255, 128, 0, 64]
    index8 = lambda x, y: ((x + y * 3) % 16,)
    emit("palette8_trns", 3, 8, index8,
         lambda x, y: PALETTE[(x + y * 3) % 16] + (alphas[(x + y * 3) % 16] if (x + y * 3) % 16 < len(alphas) else 255,),
         palette=PALETTE, trns=bytes(alphas))

    for depth in (8, 16):
        ga = lambda x, y, d=depth: (value(x, y, 1, d), value(x, y, 4, d))
        rgba = lambda x, y, d=depth: (scale(ga(x, y)[0], d),) * 3 + (scale(ga(x, y)[1], d),)
        emit(f"grayalpha{depth}", 4, depth, ga, rgba)

        full = lambda x, y, d=depth: (value(x, y, 1, d), value(x, y, 2, d), value(x, y, 3, d), value(x, y, 4, d))
        rgba = lambda x, y, d=depth: tuple(scale(v, d) for v in full(x, y))
        emit(f"rgba{depth}", 6, depth, full, rgba)
        emit(f"rgba{depth}_interlaced", 6, depth, full, rgba, interlace=True)

    # Has an alpha channel, but every pixel is opaque: the decoder should drop the mask.
    opaque = lambda x, y: (value(x, y, 1, 8), value(x, y, 2, 8), value(x, y, 3, 8), 255)
    emit("rgba8_opaque", 6, 8, opaque, opaque)

    # Smaller than one Adam7 block, so most of the seven passes are empty.
    tiny = lambda x, y: (x * 90, y * 120, 200, 255 - x * 60)
    emit("rgba8_tiny_interlaced", 6, 8, tiny, tiny, interlace=True, size=(3, 2))

    # A logo-like image for the visual checks: a coloured mark on a transparent background.
    logo = Image.new("RGBA", (240, 80), (0, 0, 0, 0))
    for y in range(80):
        for x in range(240):
            inside = (x - 40) ** 2 + (y - 40) ** 2 < 34 ** 2
            if inside:
                logo.putpixel((x, y), (31, 58, 95, 255))
            elif 88 <= x <= 230 and 24 <= y <= 56 and (x // 12) % 2 == 0:
                logo.putpixel((x, y), (200, 100, 50, 255))
    logo.save(os.path.join(IMAGES, "logo.png"))

    # JPEGs come from Pillow, which is all that is needed: they are embedded unchanged.
    photo = Image.new("RGB", (64, 48))
    for y in range(48):
        for x in range(64):
            photo.putpixel((x, y), (x * 4, y * 5, 128))
    photo.save(os.path.join(IMAGES, "baseline.jpg"), quality=90)
    photo.save(os.path.join(IMAGES, "progressive.jpg"), quality=90, progressive=True)
    photo.convert("L").save(os.path.join(IMAGES, "gray.jpg"), quality=90)
    photo.convert("CMYK").save(os.path.join(IMAGES, "cmyk.jpg"), quality=90)

    # Arithmetic coding is valid JPEG that PDF readers need not support, so the loader refuses
    # it. Only the frame marker needs to say so for the test.
    with open(os.path.join(IMAGES, "baseline.jpg"), "rb") as f:
        data = bytearray(f.read())
    sof = data.index(b"\xff\xc0")
    data[sof + 1] = 0xC9
    with open(os.path.join(IMAGES, "arithmetic.jpg"), "wb") as f:
        f.write(data)


if __name__ == "__main__":
    fonts()
    images()
    print("fixtures written to", HERE)
