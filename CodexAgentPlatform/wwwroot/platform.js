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

document.addEventListener("keydown", event => {
    if (event.key !== "Enter" || event.defaultPrevented || event.shiftKey || event.ctrlKey ||
        event.metaKey || event.altKey || event.isComposing || event.keyCode === 229) return;

    const input = event.target;
    if (!(input instanceof HTMLTextAreaElement) || !input.hasAttribute("data-send-on-enter")) return;

    const form = input.form;
    const submit = form?.querySelector('button[type="submit"]');
    if (!submit) return;

    event.preventDefault();
    if (event.repeat || !input.value.trim() || submit.matches(":disabled")) return;

    form.requestSubmit(submit);
});
