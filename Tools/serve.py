import http.server, os, sys, functools
port = int(sys.argv[1]) if len(sys.argv) > 1 else 8080
root = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "Build", "WebGL")
H = functools.partial(http.server.SimpleHTTPRequestHandler, directory=root)
H.extensions_map = {**http.server.SimpleHTTPRequestHandler.extensions_map, ".wasm": "application/wasm"}
print(f"Serving {root} on http://localhost:{port}")
http.server.ThreadingHTTPServer(("", port), H).serve_forever()
