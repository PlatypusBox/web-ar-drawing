"""
Writes a .y4m "fake webcam" clip of a hand-held coloured page, for testing the camera path:

  chrome --use-fake-device-for-media-stream --use-fake-ui-for-media-stream ^
         --use-file-for-fake-video-capture=Tools/fake_camera.y4m  http://localhost:8080/?camera=1
"""
import os, sys, math
import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
src = Image.open(os.path.join(ROOT, "Assets", "StreamingAssets", sys.argv[1] if len(sys.argv) > 1 else "test_photo_2.jpg")).convert("RGB")
W, H, N = 640, 480, 45
out = os.path.join(ROOT, "Tools", "fake_camera.y4m")
with open(out, "wb") as f:
    f.write(f"YUV4MPEG2 W{W} H{H} F30:1 Ip A1:1 C420jpeg\n".encode())
    for i in range(N):
        # tiny hand shake: zoom + shift + rotation wobble
        t = i / N * 2 * math.pi
        s = 1.0 + 0.015 * math.sin(t)
        dx, dy = 8 * math.sin(t * 2), 6 * math.cos(t * 3)
        img = src.resize((W, H), Image.BILINEAR)
        img = img.transform((W, H), Image.AFFINE, (1 / s, 0, dx, 0, 1 / s, dy), Image.BILINEAR)
        ycc = np.asarray(img.convert("YCbCr"))
        Y = ycc[..., 0]
        Cb = ycc[..., 1].reshape(H // 2, 2, W // 2, 2).mean((1, 3))
        Cr = ycc[..., 2].reshape(H // 2, 2, W // 2, 2).mean((1, 3))
        f.write(b"FRAME\n")
        f.write(Y.astype(np.uint8).tobytes()); f.write(Cb.astype(np.uint8).tobytes()); f.write(Cr.astype(np.uint8).tobytes())
print("wrote", out)
