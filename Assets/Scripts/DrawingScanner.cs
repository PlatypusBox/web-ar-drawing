using System;
using System.Collections.Generic;
using UnityEngine;

namespace WebARDrawing
{
    /// <summary>Perspective mapping from the unit square to an arbitrary quad.</summary>
    public struct Homography
    {
        public float a, b, c, d, e, f, g, h;

        // p0=TL(0,0) p1=TR(1,0) p2=BR(1,1) p3=BL(0,1)
        public static Homography SquareToQuad(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
        {
            float dx1 = p1.x - p2.x, dx2 = p3.x - p2.x, dx3 = p0.x - p1.x + p2.x - p3.x;
            float dy1 = p1.y - p2.y, dy2 = p3.y - p2.y, dy3 = p0.y - p1.y + p2.y - p3.y;
            var H = new Homography();
            if (Mathf.Abs(dx3) < 1e-6f && Mathf.Abs(dy3) < 1e-6f)
            {
                H.a = p1.x - p0.x; H.b = p3.x - p0.x; H.c = p0.x;
                H.d = p1.y - p0.y; H.e = p3.y - p0.y; H.f = p0.y;
                H.g = 0; H.h = 0;
            }
            else
            {
                float den = dx1 * dy2 - dy1 * dx2;
                if (Mathf.Abs(den) < 1e-9f) den = 1e-9f;
                H.g = (dx3 * dy2 - dy3 * dx2) / den;
                H.h = (dx1 * dy3 - dy1 * dx3) / den;
                H.a = p1.x - p0.x + H.g * p1.x;
                H.b = p3.x - p0.x + H.h * p3.x;
                H.c = p0.x;
                H.d = p1.y - p0.y + H.g * p1.y;
                H.e = p3.y - p0.y + H.h * p3.y;
                H.f = p0.y;
            }
            return H;
        }

        public Vector2 Map(float u, float v)
        {
            float w = g * u + h * v + 1f;
            if (Mathf.Abs(w) < 1e-6f) w = 1e-6f;
            return new Vector2((a * u + b * v + c) / w, (d * u + e * v + f) / w);
        }
    }

    /// <summary>Result of cutting the drawing out of the rectified page.</summary>
    public class Cutout
    {
        public Color32[] pixels;          // RGBA, top-down rows (row 0 = top)
        public int w, h;
        public Color32[] holePixels;      // paper-coloured silhouette used to "erase" the printed drawing in AR
        public int holeW, holeH;
        public RectInt holeRect;          // position of the hole patch in the rectified page (pixels, top-down)
        public RectInt fishRect;          // position of the cutout in the rectified page (pixels, top-down)
        public bool rotated;              // cutout was rotated 90 deg so the long axis is horizontal
        public int pageW, pageH;
        public Color32 paper;
    }

    public static class DrawingScanner
    {
        // ---------------------------------------------------------------- paper detection
        /// <summary>
        /// Finds the bright, low-saturation sheet of paper in a camera frame.
        /// Frame pixels use Unity's layout (row 0 = bottom). Returned quad is TL,TR,BR,BL in pixel
        /// coordinates of the same layout (y up). TL/TR/... refer to how the frame looks on screen.
        /// </summary>
        public static bool DetectPaper(Color32[] src, int w, int h, int targetW, Vector2[] quad, out float coverage)
        {
            coverage = 0f;
            if (src == null || w < 16 || h < 16) return false;
            int step = Mathf.Max(1, w / targetW);
            int gw = w / step, gh = h / step;
            int n = gw * gh;
            if (_v == null || _v.Length != n) { _v = new byte[n]; _s = new byte[n]; _cand = new bool[n]; _lab = new bool[n]; _queue = new int[n]; }
            var hist = new int[256];
            for (int y = 0; y < gh; y++)
            {
                int row = (y * step) * w;
                for (int x = 0; x < gw; x++)
                {
                    Color32 c = src[row + x * step];
                    int mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                    int mn = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                    int i = y * gw + x;
                    _v[i] = (byte)mx;
                    _s[i] = (byte)(mx == 0 ? 0 : (255 * (mx - mn)) / mx);
                    hist[mx]++;
                }
            }
            // 90th percentile of brightness => reference "paper" brightness
            int acc = 0, p90 = 255, target = (int)(n * 0.90f);
            for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= target) { p90 = i; break; } }
            if (p90 < 70) return false;
            int vThr = Mathf.Max(60, (int)(p90 * 0.78f));
            for (int i = 0; i < n; i++) _cand[i] = _v[i] >= vThr && _s[i] <= 80;

            // largest connected component of candidate pixels
            Array.Clear(_lab, 0, n);
            int bestCount = 0;
            int[] bestPix = null;
            var comp = new List<int>(n / 4);
            for (int start = 0; start < n; start++)
            {
                if (!_cand[start] || _lab[start]) continue;
                comp.Clear();
                int qh = 0, qt = 0;
                _queue[qt++] = start; _lab[start] = true;
                while (qh < qt)
                {
                    int p = _queue[qh++];
                    comp.Add(p);
                    int px = p % gw, py = p / gw;
                    if (px > 0) Push(p - 1, ref qt);
                    if (px < gw - 1) Push(p + 1, ref qt);
                    if (py > 0) Push(p - gw, ref qt);
                    if (py < gh - 1) Push(p + gw, ref qt);
                }
                if (comp.Count > bestCount) { bestCount = comp.Count; bestPix = comp.ToArray(); }
            }
            if (bestPix == null || bestCount < n * 0.06f) return false;

            // extreme points on the diagonals => 4 corners (screen orientation)
            int tl = -1, tr = -1, br = -1, bl = -1;
            int mTL = int.MaxValue, mBR = int.MinValue, mTR = int.MinValue, mBL = int.MaxValue;
            foreach (int p in bestPix)
            {
                int x = p % gw, y = p / gw;
                int dif = x - y;   // y up: small = top-left on screen
                int sum = x + y;
                if (dif < mTL) { mTL = dif; tl = p; }
                if (dif > mBR) { mBR = dif; br = p; }
                if (sum > mTR) { mTR = sum; tr = p; }
                if (sum < mBL) { mBL = sum; bl = p; }
            }
            Vector2 P(int p) => new Vector2(((p % gw) + 0.5f) * step, ((p / gw) + 0.5f) * step);
            quad[0] = P(tl); quad[1] = P(tr); quad[2] = P(br); quad[3] = P(bl);

            // sanity checks: reasonable edge lengths, convex, area vs. blob area
            float minEdge = Mathf.Min(w, h) * 0.12f;
            for (int i = 0; i < 4; i++)
                if (Vector2.Distance(quad[i], quad[(i + 1) % 4]) < minEdge) return false;
            float area = Mathf.Abs(PolyArea(quad));
            float blobArea = bestCount * step * step;
            if (area < 1f) return false;
            float fill = blobArea / area;
            if (fill < 0.50f || fill > 1.15f) return false;
            // convexity
            float sign = 0;
            for (int i = 0; i < 4; i++)
            {
                Vector2 e1 = quad[(i + 1) % 4] - quad[i];
                Vector2 e2 = quad[(i + 2) % 4] - quad[(i + 1) % 4];
                float cr = e1.x * e2.y - e1.y * e2.x;
                if (sign == 0) sign = Mathf.Sign(cr);
                else if (Mathf.Sign(cr) != sign) return false;
            }
            coverage = area / (w * (float)h);
            return true;
        }

        static byte[] _v, _s; static bool[] _cand, _lab; static int[] _queue;
        static void Push(int idx, ref int qt)
        {
            if (_cand[idx] && !_lab[idx]) { _lab[idx] = true; _queue[qt++] = idx; }
        }

        static float PolyArea(Vector2[] q)
        {
            float a = 0;
            for (int i = 0; i < 4; i++) { var p = q[i]; var r = q[(i + 1) % 4]; a += p.x * r.y - r.x * p.y; }
            return a * 0.5f;
        }

        /// <summary>Rectified page size (A4-like aspect, long side = longSide) chosen from the quad orientation.</summary>
        public static void PageSizeFor(Vector2[] quad, int longSide, out int pw, out int ph)
        {
            float wTop = Vector2.Distance(quad[0], quad[1]), wBot = Vector2.Distance(quad[3], quad[2]);
            float hL = Vector2.Distance(quad[0], quad[3]), hR = Vector2.Distance(quad[1], quad[2]);
            float qw = (wTop + wBot) * 0.5f, qh = (hL + hR) * 0.5f;
            const float A4 = 1.4142f;
            if (qw >= qh) { pw = longSide; ph = Mathf.RoundToInt(longSide / A4); }
            else { ph = longSide; pw = Mathf.RoundToInt(longSide / A4); }
        }

        // ---------------------------------------------------------------- rectification
        /// <summary>Warps the quad out of the frame into an upright page image (top-down rows).</summary>
        public static Color32[] Rectify(Color32[] src, int sw, int sh, Vector2[] quad, int outW, int outH)
        {
            var H = Homography.SquareToQuad(quad[0], quad[1], quad[2], quad[3]);
            var dst = new Color32[outW * outH];
            for (int r = 0; r < outH; r++)
            {
                float v = (r + 0.5f) / outH;
                for (int c = 0; c < outW; c++)
                {
                    float u = (c + 0.5f) / outW;
                    Vector2 p = H.Map(u, v);
                    dst[r * outW + c] = Bilinear(src, sw, sh, p.x - 0.5f, p.y - 0.5f);
                }
            }
            return dst;
        }

        static Color32 Bilinear(Color32[] s, int w, int h, float x, float y)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(x), 0, w - 1), y0 = Mathf.Clamp(Mathf.FloorToInt(y), 0, h - 1);
            int x1 = Mathf.Min(x0 + 1, w - 1), y1 = Mathf.Min(y0 + 1, h - 1);
            float fx = Mathf.Clamp01(x - x0), fy = Mathf.Clamp01(y - y0);
            Color32 a = s[y0 * w + x0], b = s[y0 * w + x1], c = s[y1 * w + x0], d = s[y1 * w + x1];
            float w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
            return new Color32(
                (byte)(a.r * w00 + b.r * w10 + c.r * w01 + d.r * w11 + 0.5f),
                (byte)(a.g * w00 + b.g * w10 + c.g * w01 + d.g * w11 + 0.5f),
                (byte)(a.b * w00 + b.b * w10 + c.b * w01 + d.b * w11 + 0.5f), 255);
        }

        // ---------------------------------------------------------------- cut the drawing out
        public static Cutout Extract(Color32[] page, int pw, int ph, out string error)
        {
            error = null;
            int n = pw * ph;

            // 1. paper colour from a ring just inside the page border
            int ring0 = Mathf.RoundToInt(Mathf.Min(pw, ph) * 0.03f), ring1 = Mathf.RoundToInt(Mathf.Min(pw, ph) * 0.07f);
            var samples = new List<Color32>();
            for (int y = ring0; y < ph - ring0; y += 2)
                for (int x = ring0; x < pw - ring0; x += 2)
                {
                    bool inRing = x < ring1 || y < ring1 || x >= pw - ring1 || y >= ph - ring1;
                    if (inRing) samples.Add(page[y * pw + x]);
                }
            if (samples.Count < 50) { error = "Page too small"; return null; }
            samples.Sort((p, q) => (p.r + p.g + p.b).CompareTo(q.r + q.g + q.b));
            int from = samples.Count * 6 / 10, to = samples.Count * 95 / 100;
            float sr = 0, sg = 0, sb = 0; int cnt = 0;
            for (int i = from; i < to; i++) { sr += samples[i].r; sg += samples[i].g; sb += samples[i].b; cnt++; }
            sr /= cnt; sg /= cnt; sb /= cnt;
            if (Mathf.Max(sr, Mathf.Max(sg, sb)) < 90) { error = "Too dark - add light"; return null; }
            float gr = 255f / Mathf.Max(sr, 1), gg = 255f / Mathf.Max(sg, 1), gb = 255f / Mathf.Max(sb, 1);

            // 2. white-balance the page so paper = white, then mark "paper-like" pixels
            var bal = new Color32[n];
            var paperLike = new bool[n];
            for (int i = 0; i < n; i++)
            {
                Color32 c = page[i];
                byte r = (byte)Mathf.Min(255, (int)(c.r * gr)), g = (byte)Mathf.Min(255, (int)(c.g * gg)), b = (byte)Mathf.Min(255, (int)(c.b * gb));
                bal[i] = new Color32(r, g, b, 255);
                int mn = Mathf.Min(r, Mathf.Min(g, b));
                paperLike[i] = mn >= 178;
            }

            // 3. flood-fill the outside paper from the page border; whatever is not reachable is "drawing"
            var bg = new bool[n];
            var queue = new int[n];
            int qh = 0, qt = 0;
            int margin = Mathf.RoundToInt(Mathf.Min(pw, ph) * 0.04f);
            for (int y = 0; y < ph; y++)
                for (int x = 0; x < pw; x++)
                {
                    if (x < margin || y < margin || x >= pw - margin || y >= ph - margin)
                    {
                        int i = y * pw + x; bg[i] = true; queue[qt++] = i;
                    }
                }
            while (qh < qt)
            {
                int p = queue[qh++];
                int x = p % pw, y = p / pw;
                if (x > 0) Spread(p - 1, bg, paperLike, queue, ref qt);
                if (x < pw - 1) Spread(p + 1, bg, paperLike, queue, ref qt);
                if (y > 0) Spread(p - pw, bg, paperLike, queue, ref qt);
                if (y < ph - 1) Spread(p + pw, bg, paperLike, queue, ref qt);
            }

            // 4. largest foreground blob (8-connected) + any sizeable neighbours (separate fins etc.)
            var lab = new int[n];
            var sizes = new List<int> { 0 };
            var minX = new List<int> { 0 }; var minY = new List<int> { 0 }; var maxX = new List<int> { 0 }; var maxY = new List<int> { 0 };
            int nLab = 0;
            for (int s = 0; s < n; s++)
            {
                if (bg[s] || lab[s] != 0) continue;
                nLab++;
                int cnt2 = 0, x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
                qh = 0; qt = 0; queue[qt++] = s; lab[s] = nLab;
                while (qh < qt)
                {
                    int p = queue[qh++]; cnt2++;
                    int x = p % pw, y = p / pw;
                    if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= pw || ny >= ph) continue;
                            int q = ny * pw + nx;
                            if (bg[q] || lab[q] != 0) continue;
                            lab[q] = nLab; queue[qt++] = q;
                        }
                }
                sizes.Add(cnt2); minX.Add(x0); minY.Add(y0); maxX.Add(x1); maxY.Add(y1);
            }
            if (nLab == 0) { error = "No drawing found on the page"; return null; }
            int big = 1;
            for (int i = 2; i <= nLab; i++) if (sizes[i] > sizes[big]) big = i;
            if (sizes[big] < n * 0.004f) { error = "Drawing too small"; return null; }
            float bigW = maxX[big] - minX[big], bigH = maxY[big] - minY[big];
            if (bigW > pw * 0.93f && bigH > ph * 0.93f) { error = "Could not separate drawing from paper (shadows?)"; return null; }

            var keep = new bool[nLab + 1];
            int bx0 = minX[big], by0 = minY[big], bx1 = maxX[big], by1 = maxY[big];
            for (int i = 1; i <= nLab; i++)
                if (i == big || sizes[i] >= sizes[big] * 0.06f)
                {
                    keep[i] = true;
                    bx0 = Mathf.Min(bx0, minX[i]); by0 = Mathf.Min(by0, minY[i]);
                    bx1 = Mathf.Max(bx1, maxX[i]); by1 = Mathf.Max(by1, maxY[i]);
                }
            var mask = new bool[n];
            for (int i = 0; i < n; i++) mask[i] = lab[i] != 0 && keep[lab[i]];

            // 5. crop + colour bleed + soft alpha
            int pad = 10;
            int cx0 = Mathf.Max(0, bx0 - pad), cy0 = Mathf.Max(0, by0 - pad);
            int cx1 = Mathf.Min(pw - 1, bx1 + pad), cy1 = Mathf.Min(ph - 1, by1 + pad);
            int cw = cx1 - cx0 + 1, chh = cy1 - cy0 + 1;

            var cm = new bool[cw * chh];
            var cc = new Color32[cw * chh];
            for (int y = 0; y < chh; y++)
                for (int x = 0; x < cw; x++)
                {
                    int s = (y + cy0) * pw + (x + cx0);
                    cm[y * cw + x] = mask[s]; cc[y * cw + x] = bal[s];
                }

            var cut = new Cutout { pageW = pw, pageH = ph, paper = new Color32((byte)sr, (byte)sg, (byte)sb, 255) };
            cut.fishRect = new RectInt(cx0, cy0, cw, chh);

            // colour bleed: let non-mask pixels near the edge take a neighbouring drawing colour (avoids white halos)
            var bled = (bool[])cm.Clone();
            for (int pass = 0; pass < 3; pass++)
            {
                var nextB = (bool[])bled.Clone();
                var nextC = (Color32[])cc.Clone();
                for (int y = 0; y < chh; y++)
                    for (int x = 0; x < cw; x++)
                    {
                        int i = y * cw + x;
                        if (bled[i]) continue;
                        for (int dy = -1; dy <= 1 && !nextB[i]; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, ny = y + dy;
                                if (nx < 0 || ny < 0 || nx >= cw || ny >= chh) continue;
                                if (bled[ny * cw + nx]) { nextC[i] = cc[ny * cw + nx]; nextB[i] = true; break; }
                            }
                    }
                bled = nextB; cc = nextC;
            }
            // alpha: slight erosion of the bleed fringe -> blur
            var alpha = new float[cw * chh];
            for (int i = 0; i < alpha.Length; i++) alpha[i] = cm[i] ? 1f : 0f;
            alpha = BoxBlur(alpha, cw, chh, 1);
            var outPix = new Color32[cw * chh];
            for (int i = 0; i < outPix.Length; i++)
            {
                Color32 c = cc[i];
                outPix[i] = new Color32(c.r, c.g, c.b, (byte)Mathf.Clamp(alpha[i] * 255f * 1.15f, 0, 255));
            }

            // hole patch (paper coloured, dilated silhouette) - kept in page orientation
            // separable max-filter dilation (the live tracking is never pixel perfect, so cover a few px extra)
            const int DR = 7;
            var tmp = new bool[cw * chh];
            var dil = new bool[cw * chh];
            for (int y = 0; y < chh; y++)
                for (int x = 0; x < cw; x++)
                {
                    bool on = false;
                    for (int dx = -DR; dx <= DR && !on; dx++) { int nx = x + dx; if (nx >= 0 && nx < cw && cm[y * cw + nx]) on = true; }
                    tmp[y * cw + x] = on;
                }
            for (int y = 0; y < chh; y++)
                for (int x = 0; x < cw; x++)
                {
                    bool on = false;
                    for (int dy = -DR; dy <= DR && !on; dy++) { int ny = y + dy; if (ny >= 0 && ny < chh && tmp[ny * cw + x]) on = true; }
                    dil[y * cw + x] = on;
                }
            var ha = new float[cw * chh];
            for (int i = 0; i < ha.Length; i++) ha[i] = dil[i] ? 1f : 0f;
            ha = BoxBlur(ha, cw, chh, 3);
            var hole = new Color32[cw * chh];
            // paper colour right around the drawing (lighting varies over the sheet)
            var pc = cut.paper;
            {
                int ex0 = Mathf.Max(0, cx0 - 18), ey0 = Mathf.Max(0, cy0 - 18), ex1 = Mathf.Min(pw - 1, cx1 + 18), ey1 = Mathf.Min(ph - 1, cy1 + 18);
                float lr = 0, lg = 0, lb = 0; int ln = 0;
                for (int y = ey0; y <= ey1; y++)
                    for (int x = ex0; x <= ex1; x++)
                    {
                        int i = y * pw + x;
                        if (mask[i] || !paperLike[i]) continue;
                        lr += page[i].r; lg += page[i].g; lb += page[i].b; ln++;
                    }
                if (ln > 50) pc = new Color32((byte)(lr / ln), (byte)(lg / ln), (byte)(lb / ln), 255);
                cut.paper = pc;
            }
            // fill the patch by interpolating the paper colour from the crop border (follows lighting gradients)
            Vector3 Edge(int x0, int y0, int dx, int dy)
            {
                Vector3 sum = Vector3.zero; int n = 0;
                for (int k = 0; k < 2; k++)
                {
                    int x = Mathf.Clamp(x0 + dx * k, 0, cw - 1), y = Mathf.Clamp(y0 + dy * k, 0, chh - 1);
                    int pi = (y + cy0) * pw + (x + cx0);
                    if (!paperLike[pi] || mask[pi]) continue;
                    sum += new Vector3(page[pi].r, page[pi].g, page[pi].b); n++;
                }
                return n > 0 ? sum / n : new Vector3(pc.r, pc.g, pc.b);
            }
            var rowL = new Vector3[chh]; var rowR = new Vector3[chh];
            var colT = new Vector3[cw]; var colB = new Vector3[cw];
            for (int y = 0; y < chh; y++) { rowL[y] = Edge(0, y, 1, 0); rowR[y] = Edge(cw - 1, y, -1, 0); }
            for (int x = 0; x < cw; x++) { colT[x] = Edge(x, 0, 0, 1); colB[x] = Edge(x, chh - 1, 0, -1); }
            for (int y = 0; y < chh; y++)
                for (int x = 0; x < cw; x++)
                {
                    int i = y * cw + x;
                    Vector3 c = 0.5f * (Vector3.Lerp(rowL[y], rowR[y], x / (float)Mathf.Max(1, cw - 1)) + Vector3.Lerp(colT[x], colB[x], y / (float)Mathf.Max(1, chh - 1)));
                    hole[i] = new Color32((byte)Mathf.Clamp(c.x, 0, 255), (byte)Mathf.Clamp(c.y, 0, 255), (byte)Mathf.Clamp(c.z, 0, 255), (byte)Mathf.Clamp(ha[i] * 255f, 0, 255));
                }
            cut.holePixels = hole; cut.holeW = cw; cut.holeH = chh; cut.holeRect = cut.fishRect;

            // 6. make the long axis horizontal (swim direction)
            if (chh > cw * 1.25f)
            {
                var rot = new Color32[outPix.Length];
                // rotate 90 deg clockwise: (x,y) -> (chh-1-y, x)
                for (int y = 0; y < chh; y++)
                    for (int x = 0; x < cw; x++)
                        rot[x * chh + (chh - 1 - y)] = outPix[y * cw + x];
                outPix = rot; cut.w = chh; cut.h = cw; cut.rotated = true;
            }
            else { cut.w = cw; cut.h = chh; }
            cut.pixels = outPix;
            return cut;
        }

        static void Spread(int idx, bool[] bg, bool[] paperLike, int[] queue, ref int qt)
        {
            if (!bg[idx] && paperLike[idx]) { bg[idx] = true; queue[qt++] = idx; }
        }

        static float[] BoxBlur(float[] a, int w, int h, int r)
        {
            var o = new float[a.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float s = 0; int c = 0;
                    for (int dy = -r; dy <= r; dy++)
                        for (int dx = -r; dx <= r; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            s += a[ny * w + nx]; c++;
                        }
                    o[y * w + x] = s / c;
                }
            return o;
        }

        /// <summary>Creates a Unity texture from top-down pixel rows.</summary>
        public static Texture2D MakeTexture(Color32[] topDown, int w, int h)
        {
            var flipped = new Color32[topDown.Length];
            for (int y = 0; y < h; y++) Array.Copy(topDown, y * w, flipped, (h - 1 - y) * w, w);
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            t.wrapMode = TextureWrapMode.Clamp; t.filterMode = FilterMode.Bilinear;
            t.SetPixels32(flipped); t.Apply(false);
            return t;
        }
    }
}
