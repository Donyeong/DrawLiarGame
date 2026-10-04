(async () => {
  const canvas = document.getElementById("unity-canvas");
  const loading = document.getElementById("loading");
  const progress = document.getElementById("progress");
  const status = document.getElementById("status");
  const korean = navigator.language.startsWith("ko");
  status.textContent = korean ? "게임 불러오는 중" : "Loading game";
  try {
    const response = await fetch("build.json", { cache: "no-cache" });
    if (!response.ok) throw new Error(korean ? "게임 파일을 불러올 수 없습니다." : "Unable to load game files.");
    const manifest = await response.json();
    const loader = document.createElement("script");
    loader.src = manifest.loaderUrl;
    await new Promise((resolve, reject) => {
      loader.onload = resolve;
      loader.onerror = () => reject(new Error(korean ? "게임 실행기를 불러올 수 없습니다." : "Unable to load the game player."));
      document.head.appendChild(loader);
    });
    window.drawLiarUnity = await createUnityInstance(canvas, {
      dataUrl: manifest.dataUrl,
      frameworkUrl: manifest.frameworkUrl,
      codeUrl: manifest.codeUrl,
      streamingAssetsUrl: manifest.streamingAssetsUrl,
      companyName: manifest.companyName,
      productName: manifest.productName,
      productVersion: manifest.productVersion,
      devicePixelRatio: Math.min(window.devicePixelRatio || 1, 2),
      showBanner: (message, type) => { if (type === "error") status.textContent = message; }
    }, value => { progress.value = value; });
    loading.hidden = true;
    canvas.focus();
  } catch (error) {
    progress.hidden = true;
    status.textContent = error instanceof Error ? error.message : String(error);
  }
})();
