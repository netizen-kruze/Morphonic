"""A very small Chrome DevTools Protocol client for driving the Morphonic
window (WebView2) started with --debug-port. Used by ui_walkthrough.py.

Needs the websocket-client package (pip install websocket-client).
"""
import base64
import json
import time
import urllib.request

import websocket


class Page:
    def __init__(self, port=9222, timeout=30.0):
        deadline = time.time() + timeout
        last = None
        while True:
            try:
                with urllib.request.urlopen("http://127.0.0.1:%d/json" % port, timeout=2) as r:
                    targets = json.load(r)
                pages = [t for t in targets if t.get("type") == "page"]
                if pages:
                    break
            except Exception as ex:  # the browser is still starting
                last = ex
            if time.time() > deadline:
                raise RuntimeError("no page on the debug port %d (%s)" % (port, last))
            time.sleep(0.3)
        self.ws = websocket.create_connection(pages[0]["webSocketDebuggerUrl"], suppress_origin=True)
        self.ws.settimeout(60)
        self._id = 0
        self.call("Runtime.enable")
        self.call("Page.enable")

    def call(self, method, **params):
        self._id += 1
        self.ws.send(json.dumps({"id": self._id, "method": method, "params": params}))
        while True:
            msg = json.loads(self.ws.recv())
            if msg.get("id") == self._id:
                if "error" in msg:
                    raise RuntimeError("%s: %s" % (method, msg["error"]))
                return msg.get("result", {})

    def eval(self, js):
        r = self.call("Runtime.evaluate", expression=js, awaitPromise=True, returnByValue=True)
        if "exceptionDetails" in r:
            raise RuntimeError("js: " + json.dumps(r["exceptionDetails"].get("exception", {}).get("description", r["exceptionDetails"])))
        return r.get("result", {}).get("value")

    def click(self, selector):
        ok = self.eval("(function(){var e=document.querySelector(%s); if(!e) return false; e.click(); return true;})()" % json.dumps(selector))
        if not ok:
            raise RuntimeError("no element " + selector)

    def text(self, selector):
        return self.eval("(function(){var e=document.querySelector(%s); return e? e.textContent.trim() : null;})()" % json.dumps(selector))

    def visible(self, selector):
        return self.eval("(function(){var e=document.querySelector(%s); if(!e) return false; var r=e.getBoundingClientRect(); return !e.hidden && r.width>0 && r.height>0 && getComputedStyle(e).visibility!='hidden';})()" % json.dumps(selector))

    def wait(self, js, timeout=30.0, every=0.25, what=None):
        deadline = time.time() + timeout
        while True:
            v = self.eval(js)
            if v:
                return v
            if time.time() > deadline:
                raise TimeoutError("timed out waiting for " + (what or js))
            time.sleep(every)

    def set_value(self, selector, value):
        self.eval("(function(){var e=document.querySelector(%s); e.value=%s; e.dispatchEvent(new Event('input',{bubbles:true})); e.dispatchEvent(new Event('change',{bubbles:true})); return true;})()" % (json.dumps(selector), json.dumps(value)))

    def screenshot(self, path):
        r = self.call("Page.captureScreenshot", format="png")
        with open(path, "wb") as f:
            f.write(base64.b64decode(r["data"]))
        return path

    def close(self):
        try:
            self.ws.close()
        except Exception:
            pass
