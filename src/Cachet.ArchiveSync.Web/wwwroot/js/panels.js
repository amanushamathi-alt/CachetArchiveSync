// Wireframe panel dividers: drag a .cachet-panel-divider to resize the two panels either side of it.
// One document-level listener, so it keeps working across Blazor re-renders and enhanced navigation.
// Display-only: widths live in inline flex-grow styles that Blazor never renders, so they are not
// reset by a re-render; a collapsed panel ignores them (app.css .is-collapsed uses !important).
(function () {
    function panelSibling(divider, direction) {
        let el = direction < 0 ? divider.previousElementSibling : divider.nextElementSibling;
        while (el && !el.classList.contains('cachet-panel')) {
            el = direction < 0 ? el.previousElementSibling : el.nextElementSibling;
        }
        return el;
    }

    document.addEventListener('mousedown', function (event) {
        const divider = event.target.closest && event.target.closest('.cachet-panel-divider');
        if (!divider || event.button !== 0) {
            return;
        }

        const left = panelSibling(divider, -1);
        const right = panelSibling(divider, 1);
        if (!left || !right || left.classList.contains('is-collapsed') || right.classList.contains('is-collapsed')) {
            return;
        }

        event.preventDefault();
        const startX = event.clientX;
        const leftWidth = left.getBoundingClientRect().width;
        const rightWidth = right.getBoundingClientRect().width;
        const total = leftWidth + rightWidth;
        const minWidth = 220;

        function onMove(moveEvent) {
            const nextLeft = Math.min(Math.max(leftWidth + (moveEvent.clientX - startX), minWidth), total - minWidth);
            left.style.flex = nextLeft + ' ' + nextLeft + ' 0%';
            right.style.flex = (total - nextLeft) + ' ' + (total - nextLeft) + ' 0%';
        }

        function onUp() {
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
            document.body.style.cursor = '';
        }

        document.body.style.cursor = 'col-resize';
        document.addEventListener('mousemove', onMove);
        document.addEventListener('mouseup', onUp);
    });
})();
