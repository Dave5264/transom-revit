"""Measure how far siding course lines jump across a vertical wall joint in an exported 3D view image.
usage: python course_offset.py IMAGE X_GUESS Y0 Y1   (pixels; the joint is the brightest vertical line within 30 px of X_GUESS)
Prints the offset as a share of one course: near 0% means the courses run straight across."""
import sys
import numpy as np
from PIL import Image
p, xg, y0, y1 = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4])
a = np.asarray(Image.open(p).convert("L"), float)
band = a[y0:y1, xg - 30:xg + 30]
jx = xg - 30 + int(np.argmax((band > 200).sum(axis=0)))
def lines(x):
    ys = np.nonzero(a[y0:y1, x] > 170)[0] + y0
    return np.array([g.mean() for g in np.split(ys, np.nonzero(np.diff(ys) > 2)[0] + 1) if len(g)])
L1, L2, R1 = lines(jx - 12), lines(jx - 6), lines(jx + 6)
n = min(len(L1), len(L2))
slope = np.median((L2[:n] - L1[:n]) / 6)
pred = L2 + slope * 12
period = np.median(np.diff(L2))
off = np.median([abs(r - pred[np.argmin(abs(pred - r))]) for r in R1])
print(f"joint x {jx}, course {period:.1f}px, offset {off:.1f}px = {off / period * 100:.0f}% of a course")
