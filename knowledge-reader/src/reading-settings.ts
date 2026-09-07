const storageKey = "feishu-reader-reading-settings-v1";
const defaults = { sidebar: 286, font: 15, width: 900 };
const clamp = (value: number, min: number, max: number): number => Math.max(min, Math.min(max, value));

export function installReadingSettings(): void {
  const settings = { ...defaults };
  try {
    const saved = JSON.parse(localStorage.getItem(storageKey) ?? "null");
    for (const key of ["sidebar", "font", "width"] as const) {
      if (saved && typeof saved[key] === "number" && Number.isFinite(saved[key])) settings[key] = saved[key];
    }
  } catch { /* Storage may be unavailable; settings still work for this session. */ }
  settings.sidebar = clamp(settings.sidebar, 220, 560);
  settings.font = clamp(settings.font, 12, 28);
  settings.width = clamp(settings.width, 600, 1600);
  const shell = document.querySelector<HTMLElement>(".shell")!;
  const separator = document.querySelector<HTMLElement>("#sidebar-resizer")!;
  const panel = document.querySelector<HTMLElement>("#reading-panel")!;
  const toggle = document.querySelector<HTMLButtonElement>("#reading-toggle")!;
  const widthInput = document.querySelector<HTMLInputElement>("#reading-width")!;
  const fontOutput = document.querySelector<HTMLOutputElement>("#reading-font-value")!;
  const widthOutput = document.querySelector<HTMLOutputElement>("#reading-width-value")!;
  const smaller = document.querySelector<HTMLButtonElement>("#reading-smaller")!;
  const larger = document.querySelector<HTMLButtonElement>("#reading-larger")!;
  const sidebarMax = (): number => Math.max(220, Math.min(560, shell.clientWidth - 430));
  const apply = (): void => {
    const sidebar = clamp(settings.sidebar, 220, sidebarMax());
    shell.style.setProperty("--sidebar-width", `${sidebar}px`);
    shell.style.setProperty("--reading-font", `${settings.font}px`);
    shell.style.setProperty("--reading-width", settings.width === 1600 ? "100%" : `${settings.width}px`);
    separator.setAttribute("aria-valuenow", String(Math.round(sidebar)));
    separator.setAttribute("aria-valuemax", String(sidebarMax()));
    fontOutput.value = `${settings.font} px`;
    widthOutput.value = settings.width === 1600 ? "填满" : `${settings.width} px`;
    widthInput.value = String(settings.width);
    smaller.disabled = settings.font <= 12;
    larger.disabled = settings.font >= 28;
  };
  const save = (): void => {
    try { localStorage.setItem(storageKey, JSON.stringify(settings)); } catch { /* Session-only fallback. */ }
  };
  const update = (): void => { apply(); save(); };
  smaller.addEventListener("click", () => { settings.font = clamp(settings.font - 1, 12, 28); update(); });
  larger.addEventListener("click", () => { settings.font = clamp(settings.font + 1, 12, 28); update(); });
  widthInput.addEventListener("input", () => { settings.width = Number(widthInput.value); update(); });
  document.querySelector("#reading-reset")!.addEventListener("click", () => {
    settings.font = defaults.font;
    settings.width = defaults.width;
    update();
  });
  const close = (): void => { panel.hidden = true; toggle.setAttribute("aria-expanded", "false"); };
  toggle.addEventListener("click", () => {
    panel.hidden = !panel.hidden;
    toggle.setAttribute("aria-expanded", String(!panel.hidden));
  });
  document.addEventListener("click", event => {
    if (!(event.target as HTMLElement).closest(".reading-settings")) close();
  });
  document.addEventListener("keydown", event => {
    if (event.key === "Escape" && !panel.hidden) { close(); toggle.focus(); }
  });
  let drag: { id: number; start: number; width: number } | null = null;
  separator.addEventListener("pointerdown", event => {
    if (event.button !== 0) return;
    event.preventDefault();
    drag = { id: event.pointerId, start: event.clientX, width: clamp(settings.sidebar, 220, sidebarMax()) };
    separator.setPointerCapture(event.pointerId);
    shell.classList.add("resizing-sidebar");
  });
  separator.addEventListener("pointermove", event => {
    if (!drag || event.pointerId !== drag.id) return;
    settings.sidebar = clamp(drag.width + event.clientX - drag.start, 220, sidebarMax());
    apply();
  });
  const finish = (): void => { drag = null; shell.classList.remove("resizing-sidebar"); save(); };
  separator.addEventListener("pointerup", finish);
  separator.addEventListener("pointercancel", finish);
  separator.addEventListener("lostpointercapture", finish);
  separator.addEventListener("dblclick", () => { settings.sidebar = defaults.sidebar; update(); });
  separator.addEventListener("keydown", event => {
    if (!["ArrowLeft", "ArrowRight", "Home"].includes(event.key)) return;
    event.preventDefault();
    settings.sidebar = event.key === "Home" ? defaults.sidebar
      : clamp(clamp(settings.sidebar, 220, sidebarMax()) + (event.key === "ArrowRight" ? 10 : -10), 220, sidebarMax());
    update();
  });
  window.addEventListener("resize", apply);
  apply();
}

export function fileTypeLabel(fileName?: string | null, assetPath?: string | null): string {
  for (const value of [fileName, assetPath]) {
    const name = (value ?? "").split(/[\\/]/).pop() ?? "";
    const match = /[^.]\.([a-z0-9]{1,12})$/i.exec(name);
    if (match) return match[1].toUpperCase();
  }
  return "文件";
}
