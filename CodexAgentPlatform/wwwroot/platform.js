window.platformChat = {
    scroll(id) {
        const pane = document.getElementById(id);
        if (!pane) return;
        if (pane.dataset.scrolled !== "true" || pane.scrollHeight - pane.scrollTop - pane.clientHeight < 240) {
            pane.scrollTop = pane.scrollHeight;
            pane.dataset.scrolled = "true";
        }
    }
};
