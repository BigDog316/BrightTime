// The consent email's link carries its token in the fragment (#token=...), which no server or
// proxy log ever sees (D34). The page it opens (/app/guardian-links/{id}/verify) needs it in its
// form, possibly after the guardian has signed in, which loses the fragment. So: take it from the
// fragment, keep it in this tab's session storage under the link's id, and drop it from the
// address; put it in the form when there is one; forget it once the link is verified.
// Without JavaScript none of this runs, and the page says so.
(() => {
    const run = () => {
        const holder = document.querySelector('[data-consent-link]');
        if (!holder) {
            return;
        }

        const key = 'brighttime.consent.' + holder.getAttribute('data-consent-link');
        const store = (() => {
            try {
                return window.sessionStorage;
            } catch {
                return null; // Storage blocked: the fragment still works on this load.
            }
        })();

        const match = /(?:^#|&)token=([^&]+)/.exec(location.hash);
        let token = match ? decodeURIComponent(match[1]) : null;
        if (match) {
            store?.setItem(key, token);
            history.replaceState(history.state, '', location.pathname + location.search);
        }

        if (holder.hasAttribute('data-consent-done')) {
            store?.removeItem(key);
            return;
        }

        token ??= store?.getItem(key) ?? null;
        const input = holder.querySelector('form input[name="token"]');
        if (input && token) {
            input.value = token;
        }
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', run);
    } else {
        run();
    }

    window.Blazor?.addEventListener('enhancedload', run);
})();
