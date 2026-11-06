// Small helpers called from Blazor components.
window.pcserv = {
    // Submits a real form, for actions that need a full page post (such as signing out).
    submitForm(formId) {
        document.getElementById(formId)?.requestSubmit();
    },

    // Returns false if the browser wouldn't allow it, which it refuses on pages without HTTPS.
    async copyText(text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            return false;
        }
    },

    // Saves generated text as a file.
    downloadText(fileName, text) {
        const url = URL.createObjectURL(new Blob([text], { type: "text/plain" }));
        const link = document.createElement("a");
        link.href = url;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        link.remove();
        URL.revokeObjectURL(url);
    },
};
