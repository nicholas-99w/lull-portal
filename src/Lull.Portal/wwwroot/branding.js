// Branding settings (Admin/Settings/Branding.cshtml): a colour picker beside each hex box, kept in
// step with it, and as either changes, how readable each colour is and a preview of a parent's
// phone in light and dark mode. Without this script the hex boxes work on their own, with no
// preview, and saving says what's wrong. The server is what decides: Branding.Check, whose rules
// this follows, as Branding.Css does for the shades. The preview is coloured through its style
// properties, which the Content-Security-Policy allows (inline styles it doesn't).
(() => {
    "use strict";
    const form = document.querySelector("form[data-min-contrast]");
    const min = Number(form.dataset.minContrast);
    const darkCard = "#1c1c1e";

    // "#0066CC", "0066cc" or "#06c" as "#0066cc"; null if it isn't a colour (as Branding.NormalizeColor).
    const normal = (value) => {
        let v = value.trim().replace(/^#/, "").toLowerCase();
        if (v.length === 3) v = [...v].map((c) => c + c).join("");
        return /^[0-9a-f]{6}$/.test(v) ? "#" + v : null;
    };
    const channels = (hex) => [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
    const luminance = (hex) => {
        const [r, g, b] = channels(hex).map((c) => {
            const v = c / 255;
            return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
        });
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    };
    const contrast = (a, b) => {
        const [la, lb] = [luminance(a), luminance(b)];
        return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
    };
    const mix = (hex, toward, amount) => {
        const to = channels(toward);
        return "#" + channels(hex).map((c, i) => Math.round(c + (to[i] - c) * amount).toString(16).padStart(2, "0")).join("");
    };
    // The nearest shade of hex, toward another colour, that passes (as Branding.Readable).
    const nearest = (hex, toward, passes) => {
        for (let step = 0; step <= 20; step++) {
            const shade = mix(hex, toward, step / 20);
            if (passes(shade)) return shade;
        }
        return toward;
    };
    const readable = (hex, background, toward) => nearest(hex, toward, (shade) => contrast(shade, background) >= min);

    const fields = [...form.querySelectorAll("[data-field]")].map((el) => ({
        el,
        name: el.dataset.field,
        text: el.querySelector("input[type=text]"),
        picker: el.querySelector("input[type=color]"),
        output: el.querySelector("output"),
    }));
    // Empty (or not a colour yet): Lull's own.
    const colorOf = (field) => normal(field.text.value) ?? field.el.dataset.default;
    const named = (name) => fields.find((f) => f.name === name);
    const color = (name) => colorOf(named(name));

    const readout = (field) => {
        if (!field.output) return;
        const { check, against, shown } = field.el.dataset;
        const own = colorOf(field);
        const other = named(against) ? color(against) : against;
        const ratio = contrast(own, other);
        const passes = ratio >= min;
        // Rounded down, so it never reads as passing when it doesn't.
        const shownRatio = `${(Math.floor(ratio * 10) / 10).toFixed(1)}:1`;
        field.output.classList.toggle("too-low", !passes);
        if (passes) {
            field.output.textContent = `✓ ${shownRatio} against ${shown}`;
            return;
        }
        const toward = check === "page" ? "#ffffff"
            : contrast(other, "#000000") > contrast(other, "#ffffff") ? "#000000" : "#ffffff";
        const suggestion = nearest(own, toward, (shade) => contrast(shade, other) >= min);
        field.output.textContent = `✕ ${shownRatio} against ${shown}: needs ${min}:1. Try ${suggestion}.`;
    };

    const previews = form.querySelector("[data-previews]");
    const paint = () => {
        const primary = color("primary"), page = color("background"), downtime = color("downtime");
        for (const phone of previews.querySelectorAll(".preview")) {
            const dark = phone.classList.contains("dark");
            const set = (name, value) => phone.style.setProperty(name, value);
            set("--accent-fill", primary);
            set("--accent", dark ? readable(primary, darkCard, "#ffffff") : readable(primary, page, "#000000"));
            set("--rest", dark ? readable(downtime, darkCard, "#ffffff") : downtime);
            if (!dark) set("--bg", page);
            set("--header-bg", color("headerBackground"));
            set("--header-text", color("headerText"));
        }
    };
    const update = () => {
        for (const field of fields) {
            field.picker.value = colorOf(field);
            readout(field);
        }
        paint();
    };

    for (const field of fields) {
        field.picker.hidden = false;
        field.picker.addEventListener("input", () => {
            field.text.value = field.picker.value;
            update();
        });
        field.text.addEventListener("input", update);
    }

    // The school's name and logo-only, as they're typed and ticked.
    const school = form.elements.schoolName;
    const logoOnly = form.elements.logoOnly;
    const words = () => {
        for (const name of previews.querySelectorAll("[data-preview-name]")) {
            name.textContent = school.value.trim() || "Lull";
            // Only with a logo to show instead, as on the real pages.
            name.hidden = logoOnly.checked && name.parentElement.querySelector("img") !== null;
        }
    };
    school.addEventListener("input", words);
    logoOnly.addEventListener("change", words);

    previews.hidden = false;
    words();
    update();
})();
