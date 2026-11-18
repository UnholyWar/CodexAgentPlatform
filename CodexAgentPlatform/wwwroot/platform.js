window.platformChat = {
    openDialog(dialog) {
        if (dialog && !dialog.open) dialog.showModal();
    },
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
    if (window.matchMedia("(pointer: coarse)").matches) return;

    const form = input.form;
    const submit = form?.querySelector('button[type="submit"]');
    if (!submit) return;

    event.preventDefault();
    if (event.repeat || !input.value.trim() || submit.matches(":disabled")) return;

    form.requestSubmit(submit);
});

document.addEventListener("click", event => {
    if (event.target instanceof HTMLDialogElement && event.target.matches(".chat-sheet")) {
        const box = event.target.getBoundingClientRect();
        if (event.clientX < box.left || event.clientX > box.right || event.clientY < box.top || event.clientY > box.bottom)
            event.target.close();
    }
    if (event.target instanceof Element && event.target.closest("#mobile-menu a")) {
        document.getElementById("mobile-menu")?.hidePopover();
    }
});

window.matchMedia("(min-width: 761px)").addEventListener("change", event => {
    if (event.matches) document.getElementById("mobile-menu")?.hidePopover();
});

// Some mobile browsers resize only the visual viewport when the keyboard opens.
function fitChatViewport() {
    const viewport = window.visualViewport;
    const chat = document.querySelector(".platform:has(.composer textarea:focus)");
    if (chat && viewport?.scale === 1 && viewport.height < window.innerHeight) {
        chat.style.setProperty("--chat-viewport", viewport.height + "px");
    } else {
        document.querySelector(".platform")?.style.removeProperty("--chat-viewport");
    }
}

window.visualViewport?.addEventListener("resize", fitChatViewport);
document.addEventListener("focusin", fitChatViewport);
document.addEventListener("focusout", () => requestAnimationFrame(fitChatViewport));
