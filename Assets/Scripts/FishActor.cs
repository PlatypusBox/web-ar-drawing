using System;
using UnityEngine;

namespace WebARDrawing
{
    /// <summary>
    /// A scanned drawing that swims. The scan itself is the texture, so the colours are exactly the ones
    /// that were coloured in on paper. Two renderings share one animation: one pinned to the paper in the
    /// camera image (AR) and one swimming around a virtual aquarium.
    /// </summary>
    public class FishActor
    {
        const int NX = 30, NY = 10;

        public Texture2D tex, holeTex;
        public readonly float wUnits, hUnits;          // fish size in "page widths"
        public readonly float pageAspect;               // page height / page width
        public readonly Rect holeRect;                  // original position of the drawing in page units (y down)
        public bool facesLeft;
        public readonly float seed;

        GameObject arGO, holeGO, tankGO;
        Mesh arMesh, holeMesh, tankMesh;
        Vector3[] verts = new Vector3[(NX + 1) * (NY + 1)];
        Vector2[] uvs = new Vector2[(NX + 1) * (NY + 1)];
        static int[] tris;

        float facing = 1f, smoothFacing = 1f, tilt;
        Vector2 prevPos; bool havePrev;
        public Vector2 pos;      // current position (page units, y down)
        public float speed = 1f;

        public FishActor(Cutout cut, Material baseMat, Transform arRoot, Transform tankRoot, int seedIndex)
        {
            seed = seedIndex * 1.7f + 0.3f;
            pageAspect = cut.pageH / (float)cut.pageW;
            wUnits = cut.w / (float)cut.pageW;
            hUnits = cut.h / (float)cut.pageW;
            holeRect = new Rect(cut.holeRect.x / (float)cut.pageW, cut.holeRect.y / (float)cut.pageW,
                                cut.holeRect.width / (float)cut.pageW, cut.holeRect.height / (float)cut.pageW);
            tex = DrawingScanner.MakeTexture(cut.pixels, cut.w, cut.h);
            holeTex = DrawingScanner.MakeTexture(cut.holePixels, cut.holeW, cut.holeH);
            if (tris == null) BuildTris();

            arGO = MakeObj("Fish_AR", arRoot, baseMat, tex, 12, out arMesh);
            holeGO = MakeObj("Hole_AR", arRoot, baseMat, holeTex, 11, out holeMesh);
            tankGO = MakeObj("Fish_Tank", tankRoot, baseMat, tex, 20 + seedIndex % 8, out tankMesh);
            arGO.SetActive(false); holeGO.SetActive(false); tankGO.SetActive(false);
            pos = new Vector2(0.5f, pageAspect * 0.5f);
        }

        static void BuildTris()
        {
            tris = new int[NX * NY * 6];
            int t = 0;
            for (int y = 0; y < NY; y++)
                for (int x = 0; x < NX; x++)
                {
                    int i = y * (NX + 1) + x;
                    tris[t++] = i; tris[t++] = i + NX + 1; tris[t++] = i + 1;
                    tris[t++] = i + 1; tris[t++] = i + NX + 1; tris[t++] = i + NX + 2;
                }
        }

        static GameObject MakeObj(string name, Transform parent, Material mat, Texture2D tex, int order, out Mesh mesh)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            mesh = new Mesh { name = name };
            mesh.MarkDynamic();
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            var m = new Material(mat); m.mainTexture = tex;
            mr.sharedMaterial = m; mr.sortingOrder = order;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return go;
        }

        public void Destroy()
        {
            UnityEngine.Object.Destroy(arGO); UnityEngine.Object.Destroy(holeGO); UnityEngine.Object.Destroy(tankGO);
            UnityEngine.Object.Destroy(tex); UnityEngine.Object.Destroy(holeTex);
        }

        public void SetVisible(bool ar, bool tank)
        {
            arGO.SetActive(ar); holeGO.SetActive(ar); tankGO.SetActive(tank);
        }

        // ------------------------------------------------------------------ motion
        /// <summary>Lissajous swim path inside a rectangle (page units, y down). Returns position + velocity.</summary>
        void Path(float t, Vector2 center, Vector2 radius, out Vector2 p, out Vector2 v)
        {
            float w1 = 0.42f * speed, w2 = 0.63f * speed;
            float a = w1 * t + seed, b = w2 * t + seed * 0.7f;
            p = new Vector2(center.x + radius.x * Mathf.Sin(a), center.y + radius.y * Mathf.Sin(b));
            v = new Vector2(radius.x * w1 * Mathf.Cos(a), radius.y * w2 * Mathf.Cos(b));
        }

        /// <summary>Updates the mesh for the AR view. toLocal maps a point in page units (y down) to the AR root space.</summary>
        public void UpdateAR(float t, Func<Vector2, Vector3> toLocal)
        {
            // keep the fish inside the sheet of paper
            var radius = new Vector2(Mathf.Max(0.04f, 0.5f - wUnits * 0.5f - 0.03f), Mathf.Max(0.03f, pageAspect * 0.5f - hUnits * 0.5f - 0.03f));
            Path(t, new Vector2(0.5f, pageAspect * 0.5f), radius, out var p, out var v);
            Pose(t, p, v, toLocal, arMesh, 1f);

            // static patch that hides the printed drawing
            BuildRectMesh(holeMesh, holeRect, toLocal, 14);
        }

        /// <summary>Updates the mesh for the aquarium view. Page units are mapped to the tank by (scale, y up).</summary>
        public void UpdateTank(float t, Vector2 tankSize, float fishWidthFraction)
        {
            float k = tankSize.x * fishWidthFraction / wUnits;   // page unit -> world
            var center = new Vector2(0f, 0f);
            var radius = new Vector2(Mathf.Max(0.1f, tankSize.x * 0.5f - wUnits * k * 0.5f - 0.2f), Mathf.Max(0.1f, tankSize.y * 0.5f * 0.55f));
            // path in world units, then convert to page units via /k so Pose works in page space
            Path(t * 0.9f, center, radius, out var p, out var v);
            Vector2 pp = p / k, vv = v / k;
            Pose(t, pp, vv, q => new Vector3(q.x * k, -q.y * k, 0f), tankMesh, 1f);
        }

        void Pose(float t, Vector2 p, Vector2 v, Func<Vector2, Vector3> toLocal, Mesh mesh, float scale)
        {
            pos = p;
            float dirX = Mathf.Abs(v.x) < 1e-4f ? facing : Mathf.Sign(v.x);
            facing = dirX;
            // smooth turn-around: x scale slides through 0
            smoothFacing = Mathf.MoveTowards(smoothFacing, facing, Time.deltaTime * 3.5f);
            float targetTilt = Mathf.Clamp(Mathf.Atan2(v.y * facing, Mathf.Abs(v.x) + 1e-5f), -0.55f, 0.55f);
            tilt = Mathf.Lerp(tilt, targetTilt, Time.deltaTime * 4f);
            float drawFlip = facesLeft ? -1f : 1f;
            float sx = smoothFacing * drawFlip;
            float cos = Mathf.Cos(tilt), sin = Mathf.Sin(tilt);
            float beat = 7.5f * speed;

            int i = 0;
            for (int y = 0; y <= NY; y++)
            {
                float ty = y / (float)NY;
                float ly = (ty - 0.5f) * hUnits;          // y down
                for (int x = 0; x <= NX; x++, i++)
                {
                    float tx = x / (float)NX;
                    float lx = (tx - 0.5f) * wUnits;
                    // s: 0 at the tail, 1 at the head (head is at +x unless drawn facing left)
                    float s = facesLeft ? 1f - tx : tx;
                    float tail = 1f - s;
                    float amp = (0.015f + 0.11f * tail * tail) * wUnits;
                    float wave = Mathf.Sin(beat * t + s * 4.2f);
                    float ly2 = ly + amp * wave;
                    // slight body squeeze while bending
                    float lx2 = lx * (1f - 0.015f * Mathf.Abs(wave) * tail);
                    // apply facing (x scale) then tilt then translate
                    float fx = lx2 * smoothFacing;
                    float rx = fx * cos - ly2 * sin;
                    float ry = fx * sin + ly2 * cos;
                    verts[i] = toLocal(new Vector2(p.x + rx, p.y + ry));
                    uvs[i] = new Vector2(tx, 1f - ty);
                }
            }
            Upload(mesh);
        }

        void BuildRectMesh(Mesh mesh, Rect r, Func<Vector2, Vector3> toLocal, int div)
        {
            int i = 0;
            // reuse the NX/NY grid layout: stretch it over the rect
            for (int y = 0; y <= NY; y++)
                for (int x = 0; x <= NX; x++, i++)
                {
                    float tx = x / (float)NX, ty = y / (float)NY;
                    verts[i] = toLocal(new Vector2(r.x + tx * r.width, r.y + ty * r.height));
                    uvs[i] = new Vector2(tx, 1f - ty);
                }
            Upload(mesh);
        }

        void Upload(Mesh mesh)
        {
            mesh.vertices = verts; mesh.uv = uvs;
            if (mesh.triangles == null || mesh.triangles.Length != tris.Length) mesh.triangles = tris;
            mesh.RecalculateBounds();
            var b = mesh.bounds; b.Expand(10f); mesh.bounds = b;
        }
    }
}
