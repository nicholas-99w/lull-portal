// The header's menus (Shared/_Layout.cshtml) are <details>, so they open and close without this
// script. It adds what people expect of a menu: a click elsewhere or Escape closes it. Served from
// the portal itself, as the Content-Security-Policy requires (SecurityHeaders.cs).
(() => {
    "use strict";
    const menus = [...document.querySelectorAll("details[data-menu]")];
    document.addEventListener("click", (e) => {
        for (const menu of menus) if (menu.open && !menu.contains(e.target)) menu.open = false;
    });
    document.addEventListener("keydown", (e) => {
        if (e.key !== "Escape") return;
        for (const menu of menus) {
            if (!menu.open) continue;
            menu.open = false;
            menu.querySelector("summary").focus();
        }
    });
})();
