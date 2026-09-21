"""Regenerates the WebP fixtures the provider-image tests embed (see ProviderWebpTests).

    python generate_fixtures.py            # run from this directory; needs Pillow with WebP support

These are REAL WebP files produced by libwebp (via Pillow), not hand-written bytes - the point of the tests is
that the Windows WebP decoder, when installed, actually decodes them, and that ArtworkImageValidator's
size/dimension bounds actually apply to them. Content is deterministic (no randomness) so a regeneration only
changes the bytes if Pillow/libwebp's encoder changes; the tests assert on decoded dimensions, never on bytes.

The oversized fixtures are solid-colour lossless images: they encode to a few hundred bytes, so they are tiny
files that nevertheless DECLARE dimensions over ArtworkImageValidator.MaxDimensionPixels (8000).
"""
import io
import os
import struct

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))


def out(name):
    return os.path.join(HERE, name)


def cover(width=600, height=900, alpha=False):
    """A recognisable cover-shaped test pattern: a diagonal gradient, a border and a few shapes."""
    mode = 'RGBA' if alpha else 'RGB'
    img = Image.new(mode, (width, height))
    px = img.load()
    for y in range(height):
        for x in range(width):
            r = (x * 255) // max(width - 1, 1)
            g = (y * 255) // max(height - 1, 1)
            b = ((x + y) * 255) // max(width + height - 2, 1)
            px[x, y] = (r, g, b, 255) if alpha else (r, g, b)
    d = ImageDraw.Draw(img)
    d.rectangle([10, 10, width - 11, height - 11], outline=(255, 255, 255, 255) if alpha else (255, 255, 255), width=6)
    d.ellipse([width // 4, height // 4, 3 * width // 4, height // 2], fill=(240, 200, 40, 255) if alpha else (240, 200, 40))
    if alpha:
        # a fully transparent corner, so the alpha channel is genuinely exercised
        d.rectangle([0, 0, width // 5, height // 8], fill=(0, 0, 0, 0))
    return img


def solid(width, height):
    return Image.new('RGB', (width, height), (30, 90, 160))


# ---- normal covers ---------------------------------------------------------------------------------------------
lossy = io.BytesIO()
cover().save(lossy, 'WEBP', quality=80, method=4)
open(out('cover-600x900-lossy.webp'), 'wb').write(lossy.getvalue())

cover().save(out('cover-600x900-lossless.webp'), 'WEBP', lossless=True, method=4)
cover(alpha=True).save(out('cover-600x900-alpha-lossless.webp'), 'WEBP', lossless=True, method=4)
Image.new('RGB', (1, 1), (255, 0, 0)).save(out('tiny-1x1-lossless.webp'), 'WEBP', lossless=True)

# ---- dimension bounds (ArtworkImageValidator.MaxDimensionPixels = 8000) ------------------------------------------
solid(9000, 100).save(out('too-wide-9000x100.webp'), 'WEBP', lossless=True, method=0)
solid(100, 9000).save(out('too-tall-100x9000.webp'), 'WEBP', lossless=True, method=0)
solid(8000, 50).save(out('at-limit-8000x50.webp'), 'WEBP', lossless=True, method=0)

# ---- animated (two frames) ---------------------------------------------------------------------------------------
frame_a = cover(120, 180)
frame_b = cover(120, 180).transpose(Image.FLIP_LEFT_RIGHT)
frame_a.save(out('animated-2-frames-120x180.webp'), 'WEBP', save_all=True, append_images=[frame_b],
             duration=120, loop=0, lossless=True)

# ---- damaged files -------------------------------------------------------------------------------------------------
whole = lossy.getvalue()
open(out('truncated-lossy.webp'), 'wb').write(whole[: int(len(whole) * 0.4)])
# A RIFF/WEBP header that declares a chunk but carries no image data at all.
open(out('riff-header-only.webp'), 'wb').write(b'RIFF' + struct.pack('<I', 4) + b'WEBP')

for name in sorted(os.listdir(HERE)):
    if name.endswith('.webp'):
        print(f'{name:38s} {os.path.getsize(out(name)):>8d} bytes')
