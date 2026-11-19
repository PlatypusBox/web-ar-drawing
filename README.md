# Magic Colouring Aquarium – Web AR drawing app (Unity WebGL)

Print a colouring page, colour it with real crayons, point your phone/laptop camera at it – the fish **comes alive with exactly
your colours** and swims across the paper (AR) or around a virtual aquarium.

Unity 6000.6.4f1 · WebGL (WebGL 2) · no third-party packages · all image processing is plain C#.

## How it works
| Step | Code |
|------|------|
| Find the sheet of paper (bright, low-saturation blob → 4 corners) | `DrawingScanner.DetectPaper` |
| Flatten the sheet with a perspective warp (homography) | `DrawingScanner.Rectify` |
| White-balance on the paper, flood-fill the outside paper, keep what's left = your coloured drawing | `DrawingScanner.Extract` |
| Turn the scan into a textured mesh and swim it (tail-wave vertex animation, turn-around, tilt) | `FishActor` |
| AR: map page coordinates through the live homography onto the camera feed, cover the printed fish with a paper-coloured patch | `ARApp.UpdateAR` |
| Aquarium: same animation in a virtual tank, up to 8 drawings at once | `ARApp.UpdateTank` |

Drawings need a **closed outline** (so white areas inside stay part of the fish) and the whole sheet visible on a plain,
contrasting surface. Drawings are assumed to face **right**; use **Flip** otherwise. Any horizontal creature works.

## Using it
1. Print `Assets/StreamingAssets/templates/fish_template_*.png` (or draw your own on plain paper), colour it.
2. Open the app, tap **Camera**, show the whole page, tap **Scan** (or turn on **Auto** and hold steady).
3. **Photo** scans a picture from your gallery, **Sample** loads a built-in test photo, **Aquarium / AR view** switches views.

Camera access needs **HTTPS or localhost** (browser rule). For phones, host the `Build/WebGL` folder on any HTTPS static host
(GitHub Pages, Netlify, itch.io …) – compression is disabled so it works anywhere.

## Build & run
```
# build (or in the editor: menu  WebAR > Build WebGL)
Unity.exe -batchmode -projectPath . -buildTarget WebGL -executeMethod WebARDrawing.EditorTools.ProjectBuilder.BuildWebGL
python Tools/serve.py          # http://localhost:8080
```

## Tests
```
Unity.exe -batchmode -nographics -projectPath . -executeMethod WebARDrawing.EditorTools.ProjectBuilder.RunScannerTest   # scanner on 3 photos
python Tools/make_fake_camera.py && python Tools/camera_test.py     # Chrome + fake webcam end-to-end
```
`python Tools/make_assets.py` regenerates the templates and the simulated photos.
