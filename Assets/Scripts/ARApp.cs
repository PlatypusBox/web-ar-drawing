using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Networking;

namespace WebARDrawing
{
    /// <summary>
    /// Web AR colouring-page app.
    ///  1. Point the camera (or load a photo) at a coloured drawing on a sheet of paper.
    ///  2. "Scan" finds the sheet, flattens it, cuts the drawing out (keeping the real colours).
    ///  3. The drawing comes alive: it swims on the paper in the camera view (AR) or in a virtual aquarium.
    /// </summary>
    public class ARApp : MonoBehaviour
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void ARSetPhotoButton(float x, float y, float w, float h, string go, string method);
        [DllImport("__Internal")] static extern void ARSetStatus(string s);
        [DllImport("__Internal")] static extern IntPtr ARGetQuery();
#else
        static void ARSetPhotoButton(float x, float y, float w, float h, string go, string method) { }
        static void ARSetStatus(string s) { }
        static IntPtr ARGetQuery() { return IntPtr.Zero; }
#endif

        public Material spriteMaterial;
        public Camera cam;

        enum View { AR, Aquarium }

        // ---- image source
        WebCamTexture webcam;
        Texture2D photo;
        Texture displayTex;
        Color32[] frame; int fw, fh;
        bool frameIsLive;

        // ---- scene
        Transform arRoot, tankRoot;
        MeshRenderer bgRenderer;
        LineRenderer outline;
        readonly List<FishActor> actors = new List<FishActor>();
        FishActor arActor;
        View view = View.AR;
        float aspect = 4f / 3f;

        // ---- tracking
        readonly Vector2[] rawQuad = new Vector2[4];
        readonly Vector2[] quad = new Vector2[4];
        bool quadValid, paperSeen; float lastSeen = -99f, detectTimer, stableTime;
        float paperCoverage;
        Homography H;
        bool autoScan;

        // ---- ui
        string status = "";
        float statusUntil;
        bool showHelp = true;
        GUIStyle btn, label, small;
        float fps, fpsTimer; int fpsFrames;
        float simTime;
        bool busy;
        string lastInfo = "";
        float infoTimer;

        // ---- tank decoration
        class Bubble { public Transform t; public float speed, wob, ph, size; }
        readonly List<Bubble> bubbles = new List<Bubble>();

        const string Template1 = "templates/fish_template_1.png";

        // =============================================================== lifecycle
        void Awake()
        {
            Application.targetFrameRate = 60;
            if (cam == null) cam = Camera.main;
            cam.orthographic = true; cam.orthographicSize = 5f;
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.04f, 0.14f, 0.26f);

            arRoot = new GameObject("ARRoot").transform;
            tankRoot = new GameObject("TankRoot").transform;
            BuildBackgroundQuad();
            BuildOutline();
            BuildTank();
            SetView(View.Aquarium);   // nothing scanned yet: show the tank behind the welcome text
        }

        IEnumerator Start()
        {
            string q = Marshal.PtrToStringAnsi(ARGetQuery()) ?? "";
            var args = ParseQuery(q);
            if (args.TryGetValue("auto", out var a) && a == "1") autoScan = true;
            if (args.TryGetValue("image", out var img))
            {
                yield return LoadPhotoFromStreaming(img);
                if (args.TryGetValue("scan", out var sc) && sc == "1") { yield return null; Scan(); }
            }
            else if (args.TryGetValue("camera", out var c) && c == "1") StartCoroutine(StartCamera());
            Publish();
        }

        static Dictionary<string, string> ParseQuery(string q)
        {
            var d = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(q)) return d;
            foreach (var kv in q.TrimStart('?').Split('&'))
            {
                var p = kv.Split('=');
                if (p.Length == 2) d[p[0]] = UnityWebRequest.UnEscapeURL(p[1]);
            }
            return d;
        }

        void OnDestroy() { if (webcam != null) webcam.Stop(); }

        // =============================================================== scene construction
        static Mesh quadMesh;
        static Mesh UnitQuad()
        {
            if (quadMesh != null) return quadMesh;
            quadMesh = new Mesh { name = "UnitQuad" };
            quadMesh.vertices = new[] { new Vector3(-.5f, -.5f), new Vector3(.5f, -.5f), new Vector3(-.5f, .5f), new Vector3(.5f, .5f) };
            quadMesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            quadMesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            quadMesh.RecalculateBounds();
            return quadMesh;
        }

        MeshRenderer MakeQuad(string name, Transform parent, Texture tex, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = UnitQuad();
            var mr = go.AddComponent<MeshRenderer>();
            var m = new Material(spriteMaterial); m.mainTexture = tex;
            mr.sharedMaterial = m; mr.sortingOrder = order;
            return mr;
        }

        void BuildBackgroundQuad()
        {
            bgRenderer = MakeQuad("CameraFeed", arRoot, Texture2D.blackTexture, 0);
            bgRenderer.enabled = false;
        }

        void BuildOutline()
        {
            var go = new GameObject("PaperOutline");
            go.transform.SetParent(arRoot, false);
            outline = go.AddComponent<LineRenderer>();
            outline.useWorldSpace = false; outline.loop = true; outline.positionCount = 4;
            outline.widthMultiplier = 0.012f;
            outline.sharedMaterial = new Material(spriteMaterial);
            outline.startColor = outline.endColor = new Color(0.3f, 1f, 0.5f, 0.9f);
            outline.sortingOrder = 30;
            outline.numCornerVertices = 4;
            outline.enabled = false;
        }

        void BuildTank()
        {
            // gradient water
            var g = new Texture2D(2, 64, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            for (int y = 0; y < 64; y++)
            {
                float t = y / 63f;
                Color c = Color.Lerp(new Color(0.02f, 0.10f, 0.24f), new Color(0.12f, 0.62f, 0.84f), t);
                g.SetPixel(0, y, c); g.SetPixel(1, y, c);
            }
            g.Apply();
            var water = MakeQuad("Water", tankRoot, g, 0); water.transform.localScale = new Vector3(40, 20, 1);

            // sand
            var s = new Texture2D(64, 8, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            for (int y = 0; y < 8; y++) for (int x = 0; x < 64; x++)
                {
                    float edge = 0.5f + 0.25f * Mathf.Sin(x * 0.35f) + 0.15f * Mathf.Sin(x * 0.9f);
                    float a = Mathf.Clamp01(edge * 6f + 1f - y);   // sand fills from the bottom up to a wavy edge
                    float n = Mathf.PerlinNoise(x * 0.4f, y * 0.4f) * 0.12f;
                    s.SetPixel(x, y, new Color(0.86f - n, 0.76f - n, 0.52f - n, a));
                }
            s.Apply();
            var sand = MakeQuad("Sand", tankRoot, s, 1); sand.name = "Sand"; sand.transform.localScale = new Vector3(40, 1.6f, 1);
            sand.transform.localPosition = new Vector3(0, -4.6f, 0);

            // light rays
            var rays = new Texture2D(64, 8, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            for (int y = 0; y < 8; y++) for (int x = 0; x < 64; x++)
                {
                    float v = Mathf.Pow(Mathf.Max(0, Mathf.Sin(x * 0.55f + y * 0.25f)), 6f) * (y / 7f) * 0.22f;
                    rays.SetPixel(x, y, new Color(1, 1, 1, v));
                }
            rays.Apply();
            var r = MakeQuad("Rays", tankRoot, rays, 2); r.transform.localScale = new Vector3(40, 12, 1); r.transform.localPosition = new Vector3(0, 4, 0);

            // bubbles
            var b = new Texture2D(32, 32, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), new Vector2(15.5f, 15.5f)) / 15f;
                    float ring = Mathf.SmoothStep(0.65f, 0.95f, d) * (1 - Mathf.SmoothStep(0.95f, 1f, d));
                    float fill = (1 - d) * 0.12f;
                    float hi = Mathf.Max(0, 1 - Vector2.Distance(new Vector2(x, y), new Vector2(11, 21)) / 4f) * 0.7f;
                    b.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(ring * 0.8f + (d < 1 ? fill : 0) + hi)));
                }
            b.Apply();
            for (int i = 0; i < 16; i++)
            {
                var q = MakeQuad("Bubble", tankRoot, b, 3);
                var bub = new Bubble { t = q.transform, speed = UnityEngine.Random.Range(0.3f, 0.9f), wob = UnityEngine.Random.Range(0.1f, 0.4f), ph = UnityEngine.Random.value * 10f, size = UnityEngine.Random.Range(0.15f, 0.5f) };
                q.transform.localScale = Vector3.one * bub.size;
                q.transform.localPosition = new Vector3(UnityEngine.Random.Range(-8f, 8f), UnityEngine.Random.Range(-5f, 5f), 0);
                bubbles.Add(bub);
            }
        }

        // =============================================================== view handling
        void SetView(View v)
        {
            view = v;
            tankRoot.gameObject.SetActive(v == View.Aquarium);
            bool showCam = v == View.AR;
            bgRenderer.enabled = showCam && displayTex != null;
            RefreshActorVisibility();
        }

        void RefreshActorVisibility()
        {
            foreach (var a in actors)
            {
                bool ar = view == View.AR && a == arActor && (quadValid && (Time.time - lastSeen < 1.5f));
                bool tank = view == View.Aquarium;
                a.SetVisible(ar, tank);
            }
        }

        void Say(string msg, float seconds = 4f) { status = msg; statusUntil = Time.time + seconds; Debug.Log("[WebAR] " + msg); }

        // =============================================================== image sources
        IEnumerator StartCamera()
        {
            if (busy) yield break;
            busy = true;
            Say("Requesting camera…", 10);
            yield return Application.RequestUserAuthorization(UserAuthorization.WebCam);
            if (!Application.HasUserAuthorization(UserAuthorization.WebCam)) { Say("Camera permission denied. You can still use Photo or Sample.", 8); busy = false; yield break; }
            var devs = WebCamTexture.devices;
            if (devs.Length == 0) { Say("No camera found. Use Photo or Sample instead.", 8); busy = false; yield break; }
            string name = devs[0].name;
            foreach (var d in devs) if (!d.isFrontFacing) { name = d.name; break; }
            if (webcam != null) webcam.Stop();
            webcam = new WebCamTexture(name, 1280, 720, 30);
            webcam.Play();
            float t0 = Time.time;
            while (webcam.width <= 16 && Time.time - t0 < 12f) yield return null;
            if (webcam.width <= 16) { Say("Camera did not start.", 8); busy = false; yield break; }
            photo = null;
            displayTex = webcam; frameIsLive = true;
            fw = webcam.width; fh = webcam.height; frame = new Color32[fw * fh];
            webcam.GetPixels32(frame);
            ApplyDisplay();
            SetView(View.AR);
            showHelp = false;
            Say("Camera on. Hold the whole sheet in view, then tap Scan.", 6);
            busy = false;
        }

        void StopCamera()
        {
            if (webcam != null) { webcam.Stop(); webcam = null; }
            frameIsLive = false;
        }

        IEnumerator LoadPhotoFromStreaming(string rel)
        {
            string url = Application.streamingAssetsPath + "/" + rel;
            yield return LoadPhoto(url);
        }

        IEnumerator LoadPhoto(string url)
        {
            Say("Loading photo…", 10);
            using (var req = UnityWebRequestTexture.GetTexture(url, false))
            {
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) { Say("Could not load image: " + req.error, 8); yield break; }
                var tex = DownloadHandlerTexture.GetContent(req);
                StopCamera();
                if (photo != null) Destroy(photo);
                photo = tex; photo.wrapMode = TextureWrapMode.Clamp;
                displayTex = photo; frameIsLive = false;
                var px = photo.GetPixels32();
                frame = DownscaleToMax(px, photo.width, photo.height, 1280, out fw, out fh);
                ApplyDisplay();
                SetView(View.AR);
                showHelp = false;
                DetectNow(true);
                Say(quadValid ? "Photo loaded. Paper found - tap Scan." : "Photo loaded, but no sheet of paper found. Try a photo of the whole page on a plain surface.", 6);
            }
        }

        // called from the JS file picker
        public void OnImagePicked(string blobUrl) { StartCoroutine(LoadPhoto(blobUrl)); }

        static Color32[] DownscaleToMax(Color32[] src, int w, int h, int max, out int ow, out int oh)
        {
            if (w <= max && h <= max) { ow = w; oh = h; return src; }
            float s = max / (float)Mathf.Max(w, h);
            ow = Mathf.Max(1, Mathf.RoundToInt(w * s)); oh = Mathf.Max(1, Mathf.RoundToInt(h * s));
            var dst = new Color32[ow * oh];
            float inv = 1f / s;
            for (int y = 0; y < oh; y++)
            {
                int y0 = Mathf.Min(h - 1, (int)(y * inv)), y1 = Mathf.Min(h - 1, Mathf.Max(y0, (int)((y + 1) * inv) - 1));
                for (int x = 0; x < ow; x++)
                {
                    int x0 = Mathf.Min(w - 1, (int)(x * inv)), x1 = Mathf.Min(w - 1, Mathf.Max(x0, (int)((x + 1) * inv) - 1));
                    int r = 0, g = 0, b = 0, n = 0;
                    for (int yy = y0; yy <= y1; yy++) for (int xx = x0; xx <= x1; xx++) { var c = src[yy * w + xx]; r += c.r; g += c.g; b += c.b; n++; }
                    dst[y * ow + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
                }
            }
            return dst;
        }

        void ApplyDisplay()
        {
            bgRenderer.sharedMaterial.mainTexture = displayTex;
            bgRenderer.enabled = view == View.AR;
            aspect = fw / (float)fh;
            quadValid = false; paperSeen = false; stableTime = 0;
        }

        // =============================================================== per-frame
        void Update()
        {
            simTime += Time.deltaTime;
            fpsFrames++; fpsTimer += Time.unscaledDeltaTime;
            if (fpsTimer > 1f) { fps = fpsFrames / fpsTimer; fpsFrames = 0; fpsTimer = 0; }

            LayoutRoots();

            if (displayTex != null)
            {
                detectTimer -= Time.deltaTime;
                if (detectTimer <= 0f) { detectTimer = frameIsLive ? 0.15f : 0.5f; DetectNow(false); }
            }

            if (view == View.AR) UpdateAR(); else UpdateTank();

            if (autoScan && displayTex != null && arActor == null && paperSeen)
            {
                stableTime += Time.deltaTime;
                if (stableTime > 1.6f) { Scan(); stableTime = 0; }
            }
            else if (!paperSeen) stableTime = 0;

            infoTimer -= Time.deltaTime;
            if (infoTimer <= 0f) { infoTimer = 0.5f; Publish(); }
        }

        void Publish()
        {
            string info = string.Format("view={0};actors={1};paper={2};coverage={3:0.00};src={4};frame={5}x{6};fps={7:0}",
                view, actors.Count, (quadValid && Time.time - lastSeen < 1.5f) ? 1 : 0, paperCoverage,
                frameIsLive ? "camera" : (photo != null ? "photo" : "none"), fw, fh, fps);
            if (info != lastInfo) { lastInfo = info; ARSetStatus(info); }
        }

        void LayoutRoots()
        {
            float sh = cam.orthographicSize * 2f, sw = sh * cam.aspect;
            float rot = 0f; bool mirrored = false;
            if (webcam != null && webcam.isPlaying) { rot = -webcam.videoRotationAngle; mirrored = webcam.videoVerticallyMirrored; }
            bool swap = Mathf.Abs(Mathf.DeltaAngle(0, rot)) > 45f && Mathf.Abs(Mathf.DeltaAngle(0, rot)) < 135f;
            float iw = swap ? 1f : aspect, ih = swap ? aspect : 1f;
            float fit = Mathf.Min(sw / iw, sh / ih);
            arRoot.localRotation = Quaternion.Euler(0, 0, rot);
            arRoot.localScale = new Vector3(fit, mirrored ? -fit : fit, 1f);
            bgRenderer.transform.localScale = new Vector3(aspect, 1f, 1f);
            tankRoot.localScale = Vector3.one;
        }

        Vector3 PixelToLocal(Vector2 px) => new Vector3((px.x / fw - 0.5f) * aspect, (px.y / fh - 0.5f), 0f);

        void DetectNow(bool force)
        {
            if (frame == null) return;
            if (frameIsLive && webcam != null && webcam.isPlaying)
            {
                if (webcam.width != fw || webcam.height != fh) { fw = webcam.width; fh = webcam.height; frame = new Color32[fw * fh]; aspect = fw / (float)fh; }
                if (!webcam.didUpdateThisFrame && !force) { /* use last pixels */ }
                webcam.GetPixels32(frame);
            }
            bool ok = DrawingScanner.DetectPaper(frame, fw, fh, 256, rawQuad, out paperCoverage);
            if (ok)
            {
                if (!quadValid || Time.time - lastSeen > 1f || !frameIsLive) for (int i = 0; i < 4; i++) quad[i] = rawQuad[i];
                else for (int i = 0; i < 4; i++) quad[i] = Vector2.Lerp(quad[i], rawQuad[i], 0.45f);
                quadValid = true; paperSeen = true; lastSeen = Time.time;
            }
            else paperSeen = false;
        }

        void UpdateAR()
        {
            bool tracking = quadValid && (Time.time - lastSeen < 1.5f);
            if (tracking)
            {
                H = Homography.SquareToQuad(quad[0], quad[1], quad[2], quad[3]);
                if (arActor != null && !outlineHiddenForActor)
                {
                    var a = arActor;
                    float pa = a.pageAspect;
                    Func<Vector2, Vector3> map = p => PixelToLocal(H.Map(p.x, p.y / pa));
                    a.UpdateAR(simTime, map);
                }
            }
            outline.enabled = tracking && displayTex != null;
            if (outline.enabled)
            {
                for (int i = 0; i < 4; i++) outline.SetPosition(i, PixelToLocal(quad[i]) + new Vector3(0, 0, -0.1f));
                outline.startColor = outline.endColor = paperSeen ? new Color(0.3f, 1f, 0.5f, 0.9f) : new Color(1f, 0.8f, 0.2f, 0.7f);
            }
            RefreshActorVisibility();
        }
        bool outlineHiddenForActor = false;

        void UpdateTank()
        {
            outline.enabled = false;
            float sh = cam.orthographicSize * 2f, sw = sh * cam.aspect;
            var size = new Vector2(sw, sh);
            for (int i = 0; i < actors.Count; i++)
            {
                var a = actors[i];
                a.speed = 0.8f + 0.1f * (i % 4);
                a.UpdateTank(simTime + i * 3.1f, size, 0.26f);
            }
            foreach (var b in bubbles)
            {
                var p = b.t.localPosition;
                p.y += b.speed * Time.deltaTime;
                p.x += Mathf.Sin(simTime * 1.3f + b.ph) * b.wob * Time.deltaTime;
                if (p.y > sh * 0.5f + 1f) { p.y = -sh * 0.5f - 0.5f; p.x = UnityEngine.Random.Range(-sw * 0.5f, sw * 0.5f); }
                b.t.localPosition = p;
            }
            RefreshActorVisibility();
        }

        // =============================================================== scanning
        public void Scan()
        {
            if (frame == null) { Say("Start the camera, load a photo or try the sample first."); return; }
            if (frameIsLive && webcam != null) webcam.GetPixels32(frame);
            var q = new Vector2[4];
            if (!DrawingScanner.DetectPaper(frame, fw, fh, 400, q, out _))
            {
                Say("Can't find the sheet of paper. Show the whole page on a plain, well-lit surface.", 6);
                return;
            }
            DrawingScanner.PageSizeFor(q, 720, out int pw, out int ph);
            var page = DrawingScanner.Rectify(frame, fw, fh, q, pw, ph);
            var cut = DrawingScanner.Extract(page, pw, ph, out string err);
            if (cut == null) { Say("Scan failed: " + err, 6); return; }

            var actor = new FishActor(cut, spriteMaterial, arRoot, tankRoot, actors.Count + 1);
            actors.Add(actor);
            if (actors.Count > 8) { actors[0].Destroy(); actors.RemoveAt(0); }
            arActor = actor;
            for (int i = 0; i < 4; i++) { quad[i] = q[i]; }
            quadValid = true; lastSeen = Time.time; paperSeen = true;
            SetView(View.AR);
            Say("Scanned! Your drawing is swimming. Tap Aquarium to see it in the tank.", 6);
            Publish();
        }

        // =============================================================== UI
        void EnsureStyles()
        {
            if (btn != null) return;
            btn = new GUIStyle(GUI.skin.button) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            label = new GUIStyle(GUI.skin.label) { fontSize = 20, wordWrap = true, alignment = TextAnchor.UpperCenter };
            label.normal.textColor = Color.white;
            small = new GUIStyle(GUI.skin.label) { fontSize = 14 };
            small.normal.textColor = new Color(1, 1, 1, 0.7f);
        }

        void OnGUI()
        {
            EnsureStyles();
            float s = Mathf.Clamp(Screen.height / 640f, 1f, 3f);
            float W = Screen.width / s, Hh = Screen.height / s;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));

            // title + status
            if (W >= 560) DrawShadowLabel(new Rect(10, 6, W - 20, 30), "Magic Colouring Aquarium", label, 22);
            if (showHelp)
            {
                string help = "1. Print a template and colour it in.\n2. Tap Camera and show the whole sheet (or use Photo / Sample).\n3. Tap Scan - your fish swims off the page with your colours!";
                float bwid = Mathf.Min(540, W - 20);
                var box = new Rect(W * 0.5f - bwid * 0.5f, Hh * 0.30f, bwid, 160);
                GUI.Box(box, GUIContent.none);
                var ls = new GUIStyle(label) { fontSize = W < 560 ? 17 : 20, alignment = TextAnchor.MiddleLeft };
                GUI.Label(new Rect(box.x + 14, box.y + 6, box.width - 28, box.height - 12), help, ls);
            }
            if (!string.IsNullOrEmpty(status) && Time.time < statusUntil)
                DrawShadowLabel(new Rect(10, W >= 560 ? 38 : 48, W - 20, 70), status, label, 18);
            else if (view == View.AR && displayTex != null)
                DrawShadowLabel(new Rect(10, 38, W - 20, 40), paperSeen ? "Paper detected - tap Scan" : (arActor != null ? "" : "Looking for the sheet of paper…"), label, 18);

            // buttons
            string[] names = { "Camera", "Photo", "Sample", "Scan", view == View.AR ? "Aquarium" : "AR view", "Flip", "Clear", "Template", autoScan ? "Auto: on" : "Auto: off" };
            int cols = W < 560 ? 3 : (W < 1100 ? 5 : 9);
            float bw = Mathf.Min(150, (W - 20 - (cols - 1) * 8) / cols), bh = 46;
            int rows = Mathf.CeilToInt(names.Length / (float)cols);
            float y0 = Hh - 10 - rows * (bh + 8) + 8;
            float totalW = cols * bw + (cols - 1) * 8, x0 = (W - totalW) * 0.5f;
            for (int i = 0; i < names.Length; i++)
            {
                var r = new Rect(x0 + (i % cols) * (bw + 8), y0 + (i / cols) * (bh + 8), bw, bh);
                if (i == 3) GUI.color = Color.Lerp(Color.white, new Color(0.5f, 1f, 0.6f), 0.7f);
                if (i == 1 && Event.current.type == EventType.Repaint)
                    ARSetPhotoButton(r.x * s, r.y * s, r.width * s, r.height * s, gameObject.name, "OnImagePicked");
                if (GUI.Button(r, names[i], btn)) OnButton(i);
                GUI.color = Color.white;
            }
            GUI.Label(new Rect(W - 130, 8, 120, 20), string.Format("{0:0} fps", fps), small);
        }

        void DrawShadowLabel(Rect r, string text, GUIStyle st, int size)
        {
            var old = st.fontSize; st.fontSize = size;
            var c = st.normal.textColor;
            st.normal.textColor = new Color(0, 0, 0, 0.8f);
            GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), text, st);
            st.normal.textColor = Color.white;
            GUI.Label(r, text, st);
            st.normal.textColor = c; st.fontSize = old;
        }

        void OnButton(int i)
        {
            switch (i)
            {
                case 0: StartCoroutine(StartCamera()); break;
                case 1: Say("Choose a photo of your coloured page…", 3); break; // picker is opened by the native pointerdown handler
                case 2: StartCoroutine(LoadPhotoFromStreaming("sample_photo.jpg")); break;
                case 3: Scan(); break;
                case 4: SetView(view == View.AR ? View.Aquarium : View.AR); break;
                case 5: if (actors.Count > 0) { var a = actors[actors.Count - 1]; a.facesLeft = !a.facesLeft; Say("Flipped swim direction."); } break;
                case 6:
                    foreach (var a in actors) a.Destroy();
                    actors.Clear(); arActor = null; Say("Cleared."); break;
                case 7: Application.OpenURL(Application.streamingAssetsPath + "/" + Template1); break;
                case 8: autoScan = !autoScan; Say(autoScan ? "Auto-scan on: hold the page steady." : "Auto-scan off."); break;
            }
        }
    }
}
