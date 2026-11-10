// Keeps a parent's pages up to date while they're open, without reloading: the home page's cards,
// and a Mac's status and controls. Every so often it asks the page for those parts alone
// (?handler=Live, with the ETag of the last answer, so an unchanged one is an empty 304) and
// swaps in any that changed, except one being used: a choice made, a field focused, or an error
// showing. It asks every 20 seconds, easing to once a minute while nothing changes, only while the
// page is visible, and stops after 10 minutes without anyone touching the page. Without this
// script the pages work as before, and a reload shows the latest. Served from the portal itself,
// as the Content-Security-Policy requires (SecurityHeaders.cs).
(() => {
    "use strict";
    const page = document.querySelector("[data-live-url]");
    if (!page || !window.fetch) return;
    const url = page.dataset.liveUrl;
    const fastest = 20_000, slowest = 60_000, idleAfter = 10 * 60_000;
    let delay = fastest, timer = null, etag = null, stopped = false, lastTouched = Date.now();

    const parts = () => [...document.querySelectorAll("[data-live]")];
    // Something on the page being used, which a swap would undo.
    const inUse = (part) => part.contains(document.activeElement) && document.activeElement !== document.body
        || part.dataset.touched === "true" || part.querySelector(".errors") !== null;
    for (const part of parts()) {
        part.addEventListener("input", () => { part.dataset.touched = "true"; });
        part.addEventListener("change", () => { part.dataset.touched = "true"; });
    }
    // What a part says, leaving out the forms' antiforgery tokens, which are new each time.
    const said = (el) => el.innerHTML.replace(/name="__RequestVerificationToken" type="hidden" value="[^"]*"/g, "");

    const paused = () => {
        if (document.querySelector(".live-paused")) return;
        const note = document.createElement("p");
        note.className = "live-paused small muted";
        note.textContent = "Updates paused while you were away. ";
        const link = document.createElement("a");
        link.href = location.pathname;
        link.textContent = "Refresh";
        note.append(link, " to see the latest.");
        page.prepend(note);
    };

    const swap = (html) => {
        const fresh = new DOMParser().parseFromString(html, "text/html");
        let changed = false;
        for (const part of parts()) {
            const next = fresh.querySelector(`[data-live="${part.dataset.live}"]`);
            if (!next || said(next) === said(part) || inUse(part)) continue;
            part.innerHTML = next.innerHTML;
            changed = true;
        }
        return changed;
    };

    const schedule = () => {
        clearTimeout(timer);
        if (!stopped) timer = setTimeout(poll, delay);
    };

    async function poll() {
        if (document.hidden) return; // visibilitychange starts it again
        if (Date.now() - lastTouched > idleAfter) {
            stopped = true;
            paused();
            return;
        }
        try {
            const headers = { "Accept": "text/html" };
            if (etag) headers["If-None-Match"] = etag;
            const res = await fetch(url, { headers, credentials: "same-origin", cache: "no-store", redirect: "manual" });
            if (res.status === 304) {
                delay = Math.min(delay * 1.5, slowest);
            } else if (res.ok && res.status === 200) {
                etag = res.headers.get("ETag");
                delay = swap(await res.text()) ? fastest : Math.min(delay * 1.5, slowest);
            } else {
                // Signed out, the Mac gone (204), or the portal down: stop, and a reload says why.
                stopped = true;
                return;
            }
        } catch {
            delay = slowest; // offline for a moment: try again later
        }
        schedule();
    }

    for (const event of ["pointerdown", "keydown", "scroll", "touchstart"])
        document.addEventListener(event, () => { lastTouched = Date.now(); }, { passive: true });
    document.addEventListener("visibilitychange", () => {
        if (document.hidden || stopped) return;
        delay = fastest;
        poll(); // catch up at once
    });
    schedule();
})();
