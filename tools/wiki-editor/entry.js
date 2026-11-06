// The wiki's editor. Built into one file, so the server needs neither Node nor internet access:
//   cd tools/wiki-editor && npm ci && npm run build
// The result goes to src/PcServ.Web/wwwroot/lib/tiptap/wiki-editor.js and is committed.
import { Editor, Node, mergeAttributes } from "@tiptap/core";
import StarterKit from "@tiptap/starter-kit";
import Image from "@tiptap/extension-image";
import { TableKit } from "@tiptap/extension-table";
import { Placeholder } from "@tiptap/extensions";

const CALLOUT_KINDS = ["note", "warning"];

// Note and warning boxes.
const Callout = Node.create({
    name: "callout",
    group: "block",
    content: "block+",
    defining: true,
    addAttributes() {
        return {
            kind: {
                default: "note",
                parseHTML: (element) => (CALLOUT_KINDS.includes(element.getAttribute("data-callout")) ? element.getAttribute("data-callout") : "note"),
                renderHTML: (attributes) => ({ "data-callout": attributes.kind }),
            },
        };
    },
    parseHTML: () => [{ tag: "div[data-callout]" }],
    renderHTML({ HTMLAttributes }) {
        return ["div", mergeAttributes(HTMLAttributes), 0];
    },
});

function isImage(file) {
    return /^image\/(jpeg|png|webp|gif)$/.test(file.type);
}

export function createEditor(toolbarElement, contentElement, html, dotNet) {
    let saveTimer = null;

    const editor = new Editor({
        element: contentElement,
        content: html || "",
        extensions: [
            StarterKit.configure({
                heading: { levels: [2, 3] },
                link: { openOnClick: false, autolink: true, protocols: ["http", "https", "mailto"], HTMLAttributes: { rel: "noopener noreferrer", target: "_blank" } },
            }),
            Image.configure({ inline: false }),
            TableKit.configure({ table: { resizable: false } }),
            Callout,
            Placeholder.configure({ placeholder: "Start writing, or paste from Google Docs..." }),
        ],
        editorProps: {
            attributes: { class: "wiki-content wiki-editor-surface", role: "textbox", "aria-multiline": "true", "aria-label": "Page content" },
            // The page's title is its top heading, so pasted headings start one level down.
            transformPastedHTML(pasted) {
                let skipped = 0;
                // The server only keeps the wiki's own images, so leave others out now rather than losing them on save.
                const withoutOutsideImages = pasted.replace(/<img\b[^>]*>/gi, (tag) => {
                    if (/src=["']wiki-images\//i.test(tag)) return tag;
                    skipped++;
                    return "";
                });
                if (skipped > 0) dotNet.invokeMethodAsync("PastedImagesSkipped", skipped);
                return withoutOutsideImages.replace(/<(\/?)h1(\s|>)/gi, "<$1h2$2").replace(/<(\/?)h[4-6](\s|>)/gi, "<$1h3$2");
            },
            handlePaste(view, event) {
                const files = [...(event.clipboardData?.files ?? [])].filter(isImage);
                // Pasted web pages can carry an image of themselves as well, so text wins.
                if (files.length === 0 || event.clipboardData.getData("text/html")) return false;
                files.forEach(upload);
                return true;
            },
            handleDrop(view, event) {
                const files = [...(event.dataTransfer?.files ?? [])].filter(isImage);
                if (files.length === 0) return false;
                files.forEach(upload);
                return true;
            },
        },
        onUpdate() {
            clearTimeout(saveTimer);
            dotNet.invokeMethodAsync("EditorChanging");
            saveTimer = setTimeout(save, 1500);
        },
        onTransaction: refreshToolbar,
        onSelectionUpdate: refreshToolbar,
    });

    function save() {
        saveTimer = null;
        return dotNet.invokeMethodAsync("EditorSave", editor.getHTML());
    }

    async function upload(file) {
        const reference = window.DotNet.createJSStreamReference(file);
        const url = await dotNet.invokeMethodAsync("UploadImage", file.name, reference);
        if (url) insertImage(url, file.name.replace(/\.[^.]+$/, ""));
    }

    function insertImage(url, alt) {
        // With a new line after it, so the next thing typed or pasted can't replace the selected image.
        editor.chain().focus().insertContent([{ type: "image", attrs: { src: url, alt: alt || "" } }, { type: "paragraph" }]).run();
    }

    function editLink() {
        const current = editor.getAttributes("link").href ?? "";
        const address = window.prompt("Link address", current || "https://");
        if (address === null) return;
        if (address.trim() === "" || address.trim() === "https://") {
            editor.chain().focus().extendMarkRange("link").unsetLink().run();
            return;
        }
        editor.chain().focus().extendMarkRange("link").setLink({ href: address.trim() }).run();
    }

    // ---- Toolbar. Buttons are labelled in words, so nothing depends on recognizing an icon. ----
    const style = document.createElement("select");
    style.className = "wiki-tool-style";
    style.setAttribute("aria-label", "Text style");
    for (const [value, label] of [["p", "Normal text"], ["2", "Heading"], ["3", "Subheading"]]) {
        const option = document.createElement("option");
        option.value = value;
        option.textContent = label;
        style.appendChild(option);
    }
    style.addEventListener("change", () => {
        const chain = editor.chain().focus();
        if (style.value === "p") chain.setParagraph().run();
        else chain.setHeading({ level: Number(style.value) }).run();
    });

    const tools = [];
    function tool(label, title, run, options = {}) {
        const button = document.createElement("button");
        button.type = "button";
        button.textContent = label;
        button.title = title;
        button.className = "wiki-tool " + (options.className ?? "");
        // Keep the text selected when a button is clicked.
        button.addEventListener("mousedown", (event) => event.preventDefault());
        button.addEventListener("click", run);
        tools.push({ button, ...options });
        return button;
    }

    const groups = [
        [style],
        [
            tool("Bold", "Bold (Ctrl+B)", () => editor.chain().focus().toggleBold().run(), { active: () => editor.isActive("bold") }),
            tool("Italic", "Italic (Ctrl+I)", () => editor.chain().focus().toggleItalic().run(), { active: () => editor.isActive("italic") }),
            tool("Underline", "Underline (Ctrl+U)", () => editor.chain().focus().toggleUnderline().run(), { active: () => editor.isActive("underline") }),
            tool("Link", "Add or change a link", editLink, { active: () => editor.isActive("link") }),
        ],
        [
            tool("Bullets", "Bulleted list", () => editor.chain().focus().toggleBulletList().run(), { active: () => editor.isActive("bulletList") }),
            tool("Numbers", "Numbered list", () => editor.chain().focus().toggleOrderedList().run(), { active: () => editor.isActive("orderedList") }),
        ],
        [
            tool("Image", "Add an image", () => dotNet.invokeMethodAsync("OpenImageLibrary")),
            tool("Table", "Add a table", () => editor.chain().focus().insertTable({ rows: 3, cols: 3, withHeaderRow: true }).run()),
            tool("Note", "Note box", () => editor.chain().focus().toggleWrap("callout", { kind: "note" }).run(), { active: () => editor.isActive("callout", { kind: "note" }) }),
            tool("Warning", "Warning box", () => editor.chain().focus().toggleWrap("callout", { kind: "warning" }).run(), { active: () => editor.isActive("callout", { kind: "warning" }) }),
            tool("Code", "Code block", () => editor.chain().focus().toggleCodeBlock().run(), { active: () => editor.isActive("codeBlock") }),
        ],
        [
            tool("Undo", "Undo (Ctrl+Z)", () => editor.chain().focus().undo().run(), { enabled: () => editor.can().undo() }),
            tool("Redo", "Redo (Ctrl+Y)", () => editor.chain().focus().redo().run(), { enabled: () => editor.can().redo() }),
        ],
        [
            tool("Add row", "Add a table row", () => editor.chain().focus().addRowAfter().run(), { tableOnly: true }),
            tool("Add column", "Add a table column", () => editor.chain().focus().addColumnAfter().run(), { tableOnly: true }),
            tool("Delete row", "Delete this table row", () => editor.chain().focus().deleteRow().run(), { tableOnly: true }),
            tool("Delete column", "Delete this table column", () => editor.chain().focus().deleteColumn().run(), { tableOnly: true }),
            tool("Delete table", "Delete this table", () => editor.chain().focus().deleteTable().run(), { tableOnly: true }),
        ],
    ];

    toolbarElement.setAttribute("role", "toolbar");
    toolbarElement.setAttribute("aria-label", "Formatting");
    for (const group of groups) {
        const wrapper = document.createElement("span");
        wrapper.className = "wiki-tool-group";
        group.forEach((item) => wrapper.appendChild(item));
        toolbarElement.appendChild(wrapper);
    }

    function refreshToolbar() {
        style.value = editor.isActive("heading", { level: 2 }) ? "2" : editor.isActive("heading", { level: 3 }) ? "3" : "p";
        const inTable = editor.isActive("table");
        for (const { button, active, enabled, tableOnly } of tools) {
            if (active) button.setAttribute("aria-pressed", active() ? "true" : "false");
            if (enabled) button.disabled = !enabled();
            if (tableOnly) button.hidden = !inTable;
        }
    }
    refreshToolbar();

    return {
        getHtml: () => editor.getHTML(),
        isEmpty: () => editor.isEmpty,
        insertImage,
        focus: () => editor.commands.focus(),
        setContent: (value) => editor.commands.setContent(value || ""),
        // Stops a waiting save, so it can't bring a draft back just after it's been published or discarded.
        cancelSave: () => {
            clearTimeout(saveTimer);
            saveTimer = null;
        },
        // Sends anything waiting to be saved. Awaited before leaving.
        flush: async () => {
            if (saveTimer !== null) {
                clearTimeout(saveTimer);
                await save();
            }
        },
        destroy() {
            clearTimeout(saveTimer);
            editor.destroy();
            toolbarElement.replaceChildren();
        },
    };
}
