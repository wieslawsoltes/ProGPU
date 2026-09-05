#!/usr/bin/env python3
"""Serve the published WebAssembly sample locally with WebGPU isolation headers."""
import argparse
import functools
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


class Handler(SimpleHTTPRequestHandler):
    def end_headers(self):
        self.send_header("Cross-Origin-Opener-Policy", "same-origin")
        self.send_header("Cross-Origin-Embedder-Policy", "require-corp")
        self.send_header("Cache-Control", "no-cache")
        super().end_headers()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=5187)
    parser.add_argument("--directory", type=Path, default=Path(__file__).resolve().parents[1] / "src/ProGPU.Samples.Suntrail.Browser/bin/Release/net10.0/publish/wwwroot")
    args = parser.parse_args()
    if not (args.directory / "index.html").is_file():
        parser.error("Publish ProGPU.Samples.Suntrail.Browser in Release first.")
    print(f"Suntrail: http://127.0.0.1:{args.port}", flush=True)
    ThreadingHTTPServer(("127.0.0.1", args.port), functools.partial(Handler, directory=str(args.directory))).serve_forever()
