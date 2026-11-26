(() => {
    const key = "platform-theme";
    const system = matchMedia("(prefers-color-scheme: dark)");
    const themeValue = value => value === "light" || value === "dark" ? value : null;
    let preference = null;
    try { preference = themeValue(localStorage.getItem(key)); } catch { }

    function apply() {
        const theme = preference ?? (system.matches ? "dark" : "light");
        document.documentElement.dataset.theme = theme;
        document.querySelectorAll("platform-theme button").forEach(button =>
            button.setAttribute("aria-checked", String(theme === "dark")));
        // The script-free license form reads only this presentation preference.
        try {
            document.cookie = key + "=" + (preference ?? "") + "; Path=/license; SameSite=Strict; Max-Age=" +
                (preference ? 31536000 : 0) + (location.protocol === "https:" ? "; Secure" : "");
        } catch { }
    }
    apply();
    customElements.define("platform-theme", class extends HTMLElement {
        connectedCallback() {
            // Parser-created elements can connect before their children exist.
            queueMicrotask(() => this.querySelector("button")?.setAttribute("aria-checked",
                String(document.documentElement.dataset.theme === "dark")));
        }
    });
    document.addEventListener("DOMContentLoaded", apply);
    document.addEventListener("click", event => {
        if (!event.target.closest?.("platform-theme button")) return;
        preference = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
        try { localStorage.setItem(key, preference); } catch { }
        apply();
    });
    system.addEventListener("change", apply);
    window.addEventListener("pageshow", () => {
        try { preference = themeValue(localStorage.getItem(key)); } catch { }
        apply();
    });
    window.addEventListener("storage", event => {
        if (event.key !== key && event.key !== null) return;
        preference = themeValue(event.newValue);
        apply();
    });
})();
