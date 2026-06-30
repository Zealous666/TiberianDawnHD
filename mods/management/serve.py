import os, sys
os.chdir(os.path.dirname(os.path.abspath(__file__)))
sys.argv = ["http.server", "7890"]
import http.server.__main__  # noqa
