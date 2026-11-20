# Builds TheExpanse/TitleScreens/Classic.png from the game's own title picture: recolours the planet behind the hex station to
# Mars tones and leaves the station as it is. Input is the game's TitleScreen_Background_v2 texture (2048 x 1159)
# saved as a PNG.
#   python make_title_screen.py <TitleScreen_Background_v2.png> <out.png> [match tolerance=0.05] [closing radius=44]
# Also writes <out>_mask.png (white = planet) and <out>_strip.png beside the output, for checking the cut-out.
import sys, numpy as np
from PIL import Image, ImageFilter
src, out = sys.argv[1], sys.argv[2]
TOL = float(sys.argv[3]) if len(sys.argv) > 3 else 0.05
im = Image.open(src).convert("RGB")
a = np.asarray(im).astype(np.float32) / 255.0
R, G, B = a[..., 0], a[..., 1], a[..., 2]
mx = a.max(-1)
L = 0.2126 * R + 0.7152 * G + 0.0722 * B
cool = (B - R) / (mx + 1e-3)
def smooth(x, lo, hi):
    t = np.clip((x - lo) / (hi - lo), 0, 1); return t * t * (3 - 2 * t)
def img(x): return Image.fromarray((np.clip(x, 0, 1) * 255).astype(np.uint8))
def arr(i): return np.asarray(i).astype(np.float32) / 255.0

# 1. Earth by colour: cool AND not dark. The station is warm/neutral (yellow paint, lit top faces) or dark (grey panels).
w = arr(img(smooth(cool, 0.03, 0.09) * smooth(L, 0.17, 0.25)).filter(ImageFilter.MedianFilter(5)))
station = img((w < 0.5).astype(np.float32))

# 2. Some lighter station panels read as "Earth". They are notches and holes in the station's outline, so close the
#    outline over them (grow then shrink by ~36 px).
station_a = arr(station) > 0.5
def grow(m, steps, shrink=False):
    # octagonal grow/shrink: alternate 8-neighbour and 4-neighbour steps so diagonal edges are treated like straight ones
    m = ~m if shrink else m.copy()
    for i in range(steps):
        p = np.pad(m, 1, mode="edge")
        n = p[:-2, 1:-1] | p[2:, 1:-1] | p[1:-1, :-2] | p[1:-1, 2:] | m
        if i % 2 == 0: n |= p[:-2, :-2] | p[:-2, 2:] | p[2:, :-2] | p[2:, 2:]
        m = n
    return ~m if shrink else m
R_CLOSE = int(sys.argv[4]) if len(sys.argv) > 4 else 44
closed_a = grow(grow(station_a, R_CLOSE), R_CLOSE, shrink=True)

# 3. Closing also swallows real planet seen through truss holes and in tight corners; that planet is bright, the
#    panels are not, so give the bright cool pixels back.
filled = closed_a & ~station_a
# ... and where it is not bright it still matches the planet right beside it: compare each swallowed pixel with the
# average colour of the certain planet nearby.
certain = (~closed_a).astype(np.float32)
num = np.stack([arr(img(a[..., i] * certain).filter(ImageFilter.GaussianBlur(30))) for i in range(3)], -1)
den = arr(img(certain).filter(ImageFilter.GaussianBlur(30)))[..., None]
near = num / np.maximum(den, 1e-3)
match = (np.abs(a - near).max(-1) < TOL) & (den[..., 0] > 0.04)
give_back = filled & (cool > 0.03) & ((L > 0.45) | match)
station_final = closed_a & ~give_back
# nothing of the station reaches the right-hand 35% of the picture; dark specks there are islands, not station
station_final[:, int(a.shape[1] * 0.655):] = False
# 3b. Thin strips of station left outside the outline (the bevelled rims of lighter panels): planet-classed pixels
#     hugging the station that are clearly darker than the open planet beyond them. Drop 1-2 px edge blends first.
band = grow(station_final, 16) & ~station_final
far = (~grow(station_final, 16)).astype(np.float32)
numL = arr(img(L * far).filter(ImageFilter.GaussianBlur(40)))
denL = arr(img(far).filter(ImageFilter.GaussianBlur(40)))
nearL = numL / np.maximum(denL, 1e-3)
strip = band & (denL > 0.02) & (L < nearL - 0.10)
strip = grow(grow(strip, 2, shrink=True), 3) & band
station_final |= strip
earth = arr(img((~station_final).astype(np.float32)).filter(ImageFilter.GaussianBlur(0.8)))
img(strip.astype(np.float32)).save(out.replace(".png", "_strip.png"))

# 4. Planet: map brightness onto a Mars ramp (umber -> rust -> orange -> pale dust).
stops = np.array([0.0, 0.18, 0.40, 0.65, 0.85, 1.0])
cols = np.array([[0.03, 0.015, 0.01], [0.26, 0.085, 0.04], [0.58, 0.23, 0.09], [0.80, 0.44, 0.22], [0.92, 0.71, 0.52], [0.98, 0.90, 0.80]])
Ls = np.clip((L - 0.10) / 0.80, 0, 1) ** 0.9
mars = np.stack([np.interp(Ls, stops, cols[:, i]) for i in range(3)], -1)

# 5. Station: its greys carry the Earth's blue bounce light; pull most of that out and warm it slightly.
k = (0.65 * smooth(cool, 0.0, 0.3))[..., None]
stn = (a + k * (L[..., None] - a)) * np.array([1.05, 1.0, 0.93])

res = stn * (1 - earth[..., None]) + mars * earth[..., None]
img(res).save(out)
img(earth).save(out.replace(".png", "_mask.png"))
