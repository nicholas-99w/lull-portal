// IT's pages, which work without this: times in the reader's own time zone (the page says UTC,
// which stays in the tooltip), and a button that copies a setup code. Served from the portal
// itself, as the Content-Security-Policy requires (SecurityHeaders.cs).
(() => {
    "use strict";
    for (const time of document.querySelectorAll("time[data-local]")) {
        const date = new Date(time.dateTime);
        if (Number.isNaN(date.getTime())) continue;
        time.title = time.textContent;
        time.textContent = new Intl.DateTimeFormat("en-AU", {
            dateStyle: "medium",
            timeStyle: time.dataset.local === "seconds" ? "medium" : "short",
        }).format(date);
    }
    if (navigator.clipboard) {
        for (const button of document.querySelectorAll("button[data-copy]")) {
            button.hidden = false;
            button.addEventListener("click", async () => {
                await navigator.clipboard.writeText(button.dataset.copy);
                button.textContent = "Copied";
            });
        }
    }
})();
