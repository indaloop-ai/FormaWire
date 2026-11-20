// Starts the editor behind the loading screen in index.html. The first visit downloads the
// .NET runtime and the editor; the browser keeps them, so later visits start almost at once.
// Starting once is remembered so the first-visit note (see index.html) is not shown again.
(function () {
  const boot = document.querySelector(".boot");
  let done = false;

  function fail(err) {
    if (done) return;
    done = true;
    console.error("FormaWire: avvio non riuscito", err);
    boot.classList.add("failed");
    boot.removeAttribute("aria-busy");
  }
  // A failed download (offline, blocked, interrupted) surfaces as an uncaught error from the
  // runtime loader while Blazor.start() stays pending, so listen for those as well.
  const onError = e => fail(e.reason ?? e.error ?? e.message);
  window.addEventListener("unhandledrejection", onError);
  window.addEventListener("error", onError);

  Blazor.start().then(() => {
    done = true;
    window.removeEventListener("unhandledrejection", onError);
    window.removeEventListener("error", onError);
    try { localStorage.setItem("formawire.started", "1"); } catch { }
  }, fail);
})();
