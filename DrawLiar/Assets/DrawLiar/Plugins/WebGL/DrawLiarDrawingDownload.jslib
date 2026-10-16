mergeInto(LibraryManager.library, {
  DrawBrowserDownloadDrawing: function (pointer, length, fileNamePointer) {
    if (pointer <= 0 || length <= 0 || length > 16777216 || pointer + length > HEAPU8.length) return 0;
    var url = null;
    var link = null;
    try {
      var name = UTF8ToString(fileNamePointer);
      if (!name || name.length > 256 || /[\\/\x00-\x1f]/.test(name) || !/\.png$/i.test(name)) return 0;
      var png = HEAPU8.slice(pointer, pointer + length);
      var blob = new Blob([png], { type: "image/png" });
      url = URL.createObjectURL(blob);
      link = document.createElement("a");
      link.href = url;
      link.download = name;
      link.style.display = "none";
      document.body.appendChild(link);
      link.click();
      return 1;
    } catch (error) {
      return 0;
    } finally {
      if (link && link.parentNode) link.parentNode.removeChild(link);
      if (url) setTimeout(function () { URL.revokeObjectURL(url); }, 60000);
    }
  }
});
