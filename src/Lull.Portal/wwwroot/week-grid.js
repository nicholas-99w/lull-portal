// Dragging across an advanced schedule (Shared/_WeekGrid.cshtml), with a mouse: press on an hour
// and drag to give every hour passed over the state that first hour changed to. The grid is plain
// checkboxes, so without this script, and on a touch screen, where a drag has to scroll the page
// past the grid, each hour toggles on its own, and the keyboard (Tab, Space) works either way.
// Served from the portal itself, as the Content-Security-Policy requires (SecurityHeaders.cs).
(() => {
    "use strict";
    for (const grid of document.querySelectorAll("[data-week-grid]")) {
        // While a pointer is down: the state being painted, and where the pointer last was.
        let paint = null;
        let last = null;
        // The press toggled its hour, so the click that follows mustn't toggle it back.
        let pressed = false;

        const cellAt = (x, y) => {
            const el = document.elementFromPoint(x, y);
            return el instanceof HTMLInputElement && el.type === "checkbox" && grid.contains(el) ? el : null;
        };

        grid.addEventListener("pointerdown", (e) => {
            const cell = e.target;
            if (e.pointerType !== "mouse" || !(cell instanceof HTMLInputElement) || cell.type !== "checkbox" || e.button !== 0) return;
            e.preventDefault(); // no text selection
            pressed = true;
            paint = !cell.checked;
            cell.checked = paint;
            cell.focus({ preventScroll: true });
            last = { x: e.clientX, y: e.clientY };
        });

        grid.addEventListener("pointermove", (e) => {
            if (paint === null) return;
            // A quick drag skips hours between two events: fill in every few pixels along the way.
            const dx = e.clientX - last.x, dy = e.clientY - last.y;
            const steps = Math.max(1, Math.ceil(Math.hypot(dx, dy) / 6));
            for (let i = 1; i <= steps; i++) {
                const cell = cellAt(last.x + (dx * i) / steps, last.y + (dy * i) / steps);
                if (cell) cell.checked = paint;
            }
            last = { x: e.clientX, y: e.clientY };
        });

        const stop = () => { paint = null; };
        window.addEventListener("pointerup", stop);
        window.addEventListener("pointercancel", stop);

        grid.addEventListener("click", (e) => {
            if (pressed && e.target instanceof HTMLInputElement) e.preventDefault();
            pressed = false;
        });
    }
})();
