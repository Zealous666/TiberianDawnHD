import os, sys
os.chdir(os.path.dirname(os.path.abspath(__file__)))
sys.argv = ["http.server", "7890"]
from http.server import SimpleHTTPRequestHandler, HTTPServer
HTTPServer(("", 7890), SimpleHTTPRequestHandler).serve_forever()
