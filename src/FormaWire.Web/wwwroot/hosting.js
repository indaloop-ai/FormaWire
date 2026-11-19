// Also used when simultaneous circuit starts race for the final server slot.
window.formawireHosting = {
  serverBusy() {
    window.location.replace(new URL("busy", document.baseURI).href);
  }
};
