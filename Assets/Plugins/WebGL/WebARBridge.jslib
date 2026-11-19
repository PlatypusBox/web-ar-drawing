mergeInto(LibraryManager.library, {

  // Opens the browser file picker (must be called from a user gesture).
  // The chosen image is exposed as a blob: URL and reported back with SendMessage.
  // Browsers only open the file picker inside a real user gesture, but Unity handles input a frame later.
  // So the app reports the on-screen rectangle of its "Photo" button (canvas pixels, y from top) every
  // frame and a native pointerdown listener opens the picker when that rectangle is hit.
  ARSetPhotoButton: function (x, y, w, h, goNamePtr, methodPtr) {
    window.__arPhotoRect = { x: x, y: y, w: w, h: h, go: UTF8ToString(goNamePtr), method: UTF8ToString(methodPtr) };
    if (window.__arPhotoInit) return;
    window.__arPhotoInit = true;
    var canvas = document.getElementById("unity-canvas") || document.querySelector("canvas");
    var input = document.createElement("input");
    input.type = "file"; input.accept = "image/*"; input.style.display = "none";
    document.body.appendChild(input);
    input.onchange = function (e) {
      var f = e.target.files && e.target.files[0];
      if (!f) return;
      var url = URL.createObjectURL(f);
      var r = window.__arPhotoRect;
      if (window.unityInstance) window.unityInstance.SendMessage(r.go, r.method, url);
    };
    window.__arFileInput = input;
    canvas.addEventListener("pointerdown", function (ev) {
      var r = window.__arPhotoRect; if (!r) return;
      var b = canvas.getBoundingClientRect();
      var px = (ev.clientX - b.left) * canvas.width / b.width;
      var py = (ev.clientY - b.top) * canvas.height / b.height;
      if (px >= r.x && px <= r.x + r.w && py >= r.y && py <= r.y + r.h) {
        input.value = "";
        input.click();
      }
    }, true);
  },

  // Lets automated tests (and the page) read what the app is doing.
  ARSetStatus: function (strPtr) {
    window.__arStatus = UTF8ToString(strPtr);
  },

  // Returns the page URL query string (e.g. "?image=...").
  ARGetQuery: function () {
    var s = window.location.search || "";
    var size = lengthBytesUTF8(s) + 1;
    var buf = _malloc(size);
    stringToUTF8(s, buf, size);
    return buf;
  },

  ARIsTouchDevice: function () {
    return ('ontouchstart' in window || navigator.maxTouchPoints > 0) ? 1 : 0;
  }
});
