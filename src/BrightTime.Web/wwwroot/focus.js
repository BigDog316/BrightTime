// Where keyboard focus goes after Blazor swaps in a new page without reloading.
//
// A full page load leaves focus at the top, where the skip link is, so this does nothing then.
// After an enhanced navigation or form post, focus would otherwise stay on the link or button
// that was activated, or fall to the body, and a screen reader would not hear that the page
// changed. So: a failed form's error summary if there is one, else the page's heading.
// Without JavaScript none of this runs, and the browser's own reload behaviour applies.
(() => {
    if (!window.Blazor) {
        return;
    }

    window.Blazor.addEventListener('enhancedload', () => {
        if (location.hash && document.getElementById(decodeURIComponent(location.hash.slice(1)))) {
            return; // The address names a target; the browser has already gone there.
        }

        const target = document.querySelector('main .error-summary') ?? document.querySelector('main h1');
        if (!target) {
            return;
        }

        if (!target.hasAttribute('tabindex')) {
            target.setAttribute('tabindex', '-1');
        }

        target.focus();
    });
})();
