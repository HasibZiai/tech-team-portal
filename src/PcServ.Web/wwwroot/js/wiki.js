// Small touches on a published wiki page. The content itself comes from the server, already cleaned.

export function enhance(article) {
    if (!article) return;
    for (const block of article.querySelectorAll("pre")) {
        if (block.parentElement?.classList.contains("wiki-code")) continue;
        const wrapper = document.createElement("div");
        wrapper.className = "wiki-code";
        block.replaceWith(wrapper);
        wrapper.appendChild(block);

        const button = document.createElement("button");
        button.type = "button";
        button.className = "wiki-tool wiki-copy";
        button.textContent = "Copy";
        button.setAttribute("aria-label", "Copy this code");
        button.addEventListener("click", async () => {
            let copied = false;
            try {
                await navigator.clipboard.writeText(block.textContent);
                copied = true;
            } catch {
                // Browsers refuse this on pages without HTTPS.
            }
            button.textContent = copied ? "Copied" : "Select it to copy";
            setTimeout(() => { button.textContent = "Copy"; }, 2000);
        });
        wrapper.appendChild(button);
    }

    if (location.hash.length > 1) scrollToHeading(decodeURIComponent(location.hash.slice(1)));
}

export function scrollToHeading(id) {
    const heading = document.getElementById(id);
    if (!heading) return;
    heading.scrollIntoView();
    // The whole path, because the app's base address would otherwise turn this into a link to the home page.
    history.replaceState(null, "", location.pathname + location.search + "#" + encodeURIComponent(id));
    heading.setAttribute("tabindex", "-1");
    heading.focus({ preventScroll: true });
}
