(() => {
    if (window.RemiPicklists) return;

    const comboboxSelector = [
        ".searchable-picklist input[role='combobox']",
        ".searchable-picklist button[role='combobox']",
        ".contract-picker input[role='combobox']"
    ].join(",");

    document.addEventListener("pointerdown", event => {
        document.querySelectorAll(comboboxSelector).forEach(combobox => {
            if (!(combobox instanceof HTMLElement)) return;
            if (combobox.getAttribute("aria-expanded") !== "true") return;
            const picker = combobox.closest(".searchable-picklist, .contract-picker");
            if (event.target instanceof Node && picker?.contains(event.target)) return;

            if (combobox === document.activeElement) combobox.blur();
            else if (combobox.matches("button.remi-picklist-trigger")) combobox.click();
            else {
                combobox.focus({ preventScroll: true });
                combobox.blur();
            }
        });
    }, true);

    const scrollSelectedOptions = () => {
        document.querySelectorAll(".searchable-picklist-options[data-scroll-selected='true']").forEach(list => {
            const selected = list.querySelector("[role='option'][aria-selected='true']");
            if (selected instanceof HTMLElement) {
                list.scrollTop = Math.max(0, selected.offsetTop - ((list.clientHeight - selected.offsetHeight) / 2));
            }
            list.dataset.scrollSelected = "false";
        });
    };

    new MutationObserver(scrollSelectedOptions).observe(document.body, { childList: true, subtree: true });
    window.RemiPicklists = { scrollSelectedOptions };
})();
