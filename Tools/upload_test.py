"""
Tests the "Photo" upload path (file input -> blob URL -> Unity) with Chrome DevTools, then lets Auto-scan
turn the picked photo into a swimming drawing.   python Tools/upload_test.py [http://localhost:8080] [photo]
"""
import base64, json, os, subprocess, sys, tempfile, time, urllib.request
import websocket

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BASE = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:8080"
PHOTO = os.path.abspath(sys.argv[2]) if len(sys.argv) > 2 else os.path.join(ROOT, "Assets", "StreamingAssets", "test_photo_3.jpg")
CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"
OUT = os.path.join(ROOT, "Build", "CameraTest"); os.makedirs(OUT, exist_ok=True)
port = 9334
proc = subprocess.Popen([CHROME, "--headless=new", f"--remote-debugging-port={port}", f"--user-data-dir={tempfile.mkdtemp()}",
    "--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--ignore-gpu-blocklist", "--window-size=1000,700", "about:blank"],
    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
try:
    for _ in range(50):
        try: tabs = json.load(urllib.request.urlopen(f"http://127.0.0.1:{port}/json")); break
        except Exception: time.sleep(0.2)
    ws = websocket.create_connection([t for t in tabs if t["type"] == "page"][0]["webSocketDebuggerUrl"], timeout=30)
    mid = [0]
    def call(method, **p):
        mid[0] += 1; ws.send(json.dumps({"id": mid[0], "method": method, "params": p}))
        while True:
            m = json.loads(ws.recv())
            if m.get("id") == mid[0]: return m.get("result", {})
    def ev(expr):
        return call("Runtime.evaluate", expression=expr, returnByValue=True).get("result", {}).get("value")
    call("Runtime.enable"); call("Page.enable"); call("DOM.enable")
    call("Page.navigate", url=BASE + "/?auto=1")
    for _ in range(40):
        time.sleep(1)
        if ev("!!window.__arFileInput"): break
    print("file input present:", ev("!!window.__arFileInput"), "| status:", ev("window.__arStatus"))
    node = call("Runtime.evaluate", expression="window.__arFileInput")["result"]["objectId"]
    call("DOM.setFileInputFiles", files=[PHOTO], objectId=node)
    ok = False
    for _ in range(30):
        time.sleep(1.5); s = ev("window.__arStatus"); print(s)
        if s and "src=photo" in s and "actors=1" in s: ok = True; break
    time.sleep(2)
    open(os.path.join(OUT, "upload_ar.png"), "wb").write(base64.b64decode(call("Page.captureScreenshot", format="png")["data"]))
    print("UPLOAD TEST", "PASSED" if ok else "FAILED"); sys.exit(0 if ok else 1)
finally:
    proc.terminate()
