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

function mentionRange(input) {
    if (input.selectionStart !== input.selectionEnd) return null;
    const end = input.selectionStart;
    const before = input.value.slice(0, end);
    const start = before.lastIndexOf("@");
    if (start < 0 || (start > 0 && !/\s/u.test(before[start - 1]))) return null;
    const query = before.slice(start + 1);
    if (query.length > 60 || /[\r\n]/.test(query)) return null;
    return { start, end, query };
}

function hideMentions(input, list) {
    list.hidden = true;
    input.removeAttribute("aria-activedescendant");
}

function highlightMention(input, list, option) {
    for (const item of list.children) item.setAttribute("aria-selected", String(item === option));
    input.setAttribute("aria-activedescendant", option.id);
    option.scrollIntoView({ block: "nearest" });
}

function updateMentions(input) {
    const list = input.form?.querySelector(".mention-list");
    if (!list) return;
    const range = mentionRange(input);
    if (!range) return hideMentions(input, list);
    const query = range.query.toLocaleLowerCase();
    for (const option of list.children) {
        option.hidden = !option.dataset.agentLabel.toLocaleLowerCase().includes(query);
    }
    const first = list.querySelector('[role="option"]:not([hidden])');
    if (!first) return hideMentions(input, list);
    list.hidden = false;
    highlightMention(input, list, first);
}

document.addEventListener("input", event => {
    if (!event.isComposing && event.target.matches("textarea[data-send-on-enter]")) updateMentions(event.target);
});
document.addEventListener("focusout", event => {
    const input = event.target;
    const list = input.matches("textarea[data-send-on-enter]") && input.form?.querySelector(".mention-list");
    if (list && !list.contains(event.relatedTarget)) hideMentions(input, list);
});
document.addEventListener("pointerdown", event => {
    if (event.target.closest?.(".mention-list [role=option]")) event.preventDefault();
});
document.addEventListener("click", event => {
    const option = event.target.closest?.(".mention-list [role=option]");
    if (!option) return;
    const list = option.parentElement;
    const input = option.form.querySelector("textarea");
    const range = mentionRange(input);
    if (!range || list.hidden || option.hidden) return;
    input.setRangeText("", range.start, range.end, "end");
    input.dispatchEvent(new Event("input", { bubbles: true }));
    hideMentions(input, list);
    input.focus({ preventScroll: true });
}, true);

document.addEventListener("keydown", event => {
    if (event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey ||
        event.isComposing || event.keyCode === 229) return;

    const input = event.target;
    if (!(input instanceof HTMLTextAreaElement) || !input.hasAttribute("data-send-on-enter")) return;
    const list = input.form?.querySelector(".mention-list");
    if (list && !list.hidden) {
        const active = list.querySelector('[aria-selected="true"]:not([hidden])');
        if (event.key === "Escape" || (event.key === "Tab" && event.shiftKey)) {
            hideMentions(input, list);
            if (event.key === "Escape") event.preventDefault();
            return;
        }
        if (active && !event.shiftKey) {
            if (event.key === "ArrowDown" || event.key === "ArrowUp") {
                event.preventDefault();
                const options = Array.from(list.children).filter(option => !option.hidden);
                const offset = event.key === "ArrowDown" ? 1 : -1;
                highlightMention(input, list, options[(options.indexOf(active) + offset + options.length) % options.length]);
                return;
            }
            if (event.key === "Tab" || event.key === "Enter") {
                event.preventDefault();
                if (!event.repeat) active.click();
                return;
            }
        }
    }
    if (event.key !== "Enter" || event.shiftKey) return;
    if (window.matchMedia("(pointer: coarse)").matches) return;

    const form = input.form;
    const submit = form?.querySelector('button[type="submit"]');
    if (!submit) return;

    event.preventDefault();
    if (event.repeat || submit.matches(":disabled")) return;

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
