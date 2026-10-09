// CAS-039 §2.3 A1: opens the browser's native date picker for a hidden <input type="date">. Uses
// showPicker() where the browser supports it; otherwise (or if it throws) falls back to focusing and
// clicking the input. Typing into the MM/dd/yyyy text field always stays available either way.
// A single delegated click listener opens the input named by a button's data-cachet-date-picker-for,
// so the call runs inside the user's own click (showPicker() needs that user activation, which a
// Blazor SignalR round trip could let expire) and no inline script is needed (CSP-friendly).
window.cachetDatePicker = {
    open: function (element) {
        if (!element) return;
        try {
            if (typeof element.showPicker === 'function') {
                element.showPicker();
                return;
            }
        } catch (e) {
            /* fall through to the focus + click fallback */
        }
        element.focus();
        element.click();
    }
};

if (!window.cachetDatePickerListenerRegistered) {
    window.cachetDatePickerListenerRegistered = true;
    document.addEventListener('click', function (e) {
        var btn = e.target && e.target.closest ? e.target.closest('[data-cachet-date-picker-for]') : null;
        if (!btn) return;
        window.cachetDatePicker.open(document.getElementById(btn.getAttribute('data-cachet-date-picker-for')));
    });
}
