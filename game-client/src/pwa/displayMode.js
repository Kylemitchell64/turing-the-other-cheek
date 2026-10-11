// True when running as an installed app (home-screen PWA) rather than a browser tab.
// iOS Safari only exposes the legacy navigator.standalone flag; everyone else supports
// the display-mode media query from the manifest.
export function isInstalledApp() {
  try {
    return (
      window.matchMedia?.("(display-mode: standalone)").matches === true ||
      window.navigator.standalone === true
    );
  } catch {
    return false;
  }
}
