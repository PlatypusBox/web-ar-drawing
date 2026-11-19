"""
End-to-end camera test: launches Chrome with a fake webcam (Tools/fake_camera.y4m), opens the built
WebGL app with ?camera=1&auto=1 and checks that the paper is tracked, the drawing is auto-scanned
and animated.   Requires:  pip install websocket-client pillow numpy

    python Tools/make_fake_camera.py
    python Tools/camera_test.py [http://localhost:8080]
"""
import base64, json, os, subprocess, sys, tempfile, time, urllib.request
import websocket

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
URL = (sys.argv[1] if len(sys.argv) > 1 else "http://localhost:8080") + "/?camera=1&auto=1"
CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"
OUT = os.path.join(ROOT, "Build", "CameraTest"); os.makedirs(OUT, exist_ok=True)
port = 9333
prof = tempfile.mkdtemp()
proc = subprocess.Popen([CHROME, "--headless=new", f"--remote-debugging-port={port}", f"--user-data-dir={prof}",
    "--use-fake-device-for-media-stream", "--use-fake-ui-for-media-stream",
    f"--use-file-for-fake-video-capture={os.path.join(ROOT, 'Tools', 'fake_camera.y4m')}",
    "--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--ignore-gpu-blocklist",
    "--window-size=1000,700", "--no-first-run", "about:blank"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
try:
    for _ in range(50):
        try:
            tabs = json.load(urllib.request.urlopen(f"http://127.0.0.1:{port}/json")); break
        except Exception:
            time.sleep(0.2)
    ws = websocket.create_connection([t for t in tabs if t["type"] == "page"][0]["webSocketDebuggerUrl"], timeout=30)
    mid = [0]
    logs = []
    def call(method, **params):
        mid[0] += 1; ws.send(json.dumps({"id": mid[0], "method": method, "params": params}))
        while True:
            m = json.loads(ws.recv())
            if m.get("method") == "Runtime.consoleAPICalled":
                logs.append(" ".join(str(a.get("value", a.get("description", ""))) for a in m["params"]["args"]))
            if m.get("id") == mid[0]: return m.get("result", {})
    call("Runtime.enable"); call("Page.enable")
    call("Page.navigate", url=URL)
    def status():
        r = call("Runtime.evaluate", expression="window.__arStatus", returnByValue=True)
        return r.get("result", {}).get("value")
    def shot(name):
        r = call("Page.captureScreenshot", format="png")
        open(os.path.join(OUT, name), "wb").write(base64.b64decode(r["data"]))
    seen = []
    t0 = time.time(); ok = False
    while time.time() - t0 < 90:
        time.sleep(2)
        s = status(); seen.append(s); print(f"{time.time()-t0:5.1f}s", s)
        if s and "actors=1" in s and "src=camera" in s and "paper=1" in s:
            ok = True; break
    time.sleep(3); shot("camera_ar.png")
    print("final:", status())
    print("\n".join(l for l in logs if "[WebAR]" in l))
    print("CAMERA TEST", "PASSED" if ok else "FAILED")
    sys.exit(0 if ok else 1)
finally:
    proc.terminate()
