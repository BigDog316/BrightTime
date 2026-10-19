# Keyboard-only pass

The M1 acceptance asks for "axe-core clean on every public route; keyboard-only pass
documented". axe can't judge focus order, visible focus, skip links or traps. This pass checks
them, and `tests/BrightTime.E2E.Tests/KeyboardTests.cs` keeps them true in CI.

Done 2026-10-19, against the seeded Kettle County site, in Chromium. JavaScript was on, and
off where the behaviour differs.

## What was checked

| Check | How |
|---|---|
| Every public route ([`PublicRoutes`](../../tests/BrightTime.E2E.Tests/PublicRoutes.cs), 18 routes including empty, filtered, map and missing states) | Tab from page load to past the last control |
| The skip link is the first stop and moves focus into `<main>` | Tab, Enter, Tab; with and without JavaScript |
| Every stop shows a focus indicator and is on screen | Computed outline or box-shadow at each stop; for map areas, the shape's border |
| No keyboard trap | Each walk must leave the page or wrap within 400 stops |
| Order follows the page | Read in full for search, listing, map and sign-in pages |
| Console flows by keyboard alone | Dev sign-in, header to review queue, open a listing, approve it; provider saves an empty listing and recovers from the errors |
| Focus after a page change | A fresh load, an in-app (enhanced) navigation, and a failed form post |

## What was found and fixed

1. **The skip link left the page.** `<base href="/">` resolves `href="#main"` to `/#main`, so
   on every page but home, "Skip to main content" loaded the home page.
   - Fixed by `SamePageLink`, which writes the page's own path and query before the fragment.
   - It opts out of Blazor's enhanced navigation, which would scroll to the target but leave
     keyboard focus behind.
2. **Error links lost the form.** The same base-href bug sent every "Check these fields" link,
   and the listing editor's section menu, to the home page. After a failed post, what the
   person had typed was gone. These links now use `SamePageLink` too, and land on their field.
3. **Keyboard users couldn't reach the skip link or header.** Blazor's `FocusOnNavigate`
   focused the `<h1>` on every load, so the first Tab went into the page past the skip link,
   header and language picker. It is replaced by `wwwroot/focus.js`, which acts only after
   an in-app navigation:
   - It moves focus to a failed form's error summary if there is one, otherwise the new
     page's `<h1>`, so a screen reader announces the change.
   - A fresh load leaves focus at the top.
4. **A failed form didn't say so.** After a failed post, focus started at the top of the page.
   Every error summary now has `autofocus`, so the page opens on the list of problems, with or
   without JavaScript. Tab reaches each link, and Enter puts focus in the field.
5. **The map's focus ring was mostly hidden.** Neighbouring areas, drawn later in the SVG,
   painted over the focused area's border on two or three sides.
   - Each area is now clipped to its own shape, so nothing paints outside it.
   - The focused area shows a 3px inner border on every side, in screen pixels however small
     the map is drawn.
6. **The error banner's dismiss control wasn't focusable.** It was a `<span role="button">`
   with no `tabindex`, and is now a `<button>`.

## What it is like now

- **Every public page:** the first Tab shows "Skip to main content", and Enter then Tab
  reaches the page's first control (the search box on search pages).
- **Order:** header (brand, console links when signed in, sign-out, languages), then the
  page in reading order, then the footer.
- **Search:** the filters come in visual order, then Search, Clear filters, the list/map
  switch, the results and "Show more results".
- **Focus is always visible:** a 3px accent outline everywhere, and the inner border on the
  map.
- **Language picker:** six links, each named in its own language. Arabic flips the page to
  right-to-left. Its walk had no problems, but whether the order reads naturally right to left
  hasn't been judged by an Arabic reader.
- **Forms:** native controls only, so Space toggles checkboxes, arrows move through selects,
  and Enter submits.
- **Console:** sign-in, navigation, the review queue, approving and the listing editor all
  work by keyboard. The editor is long (about 140 stops to "Save draft"), and its section
  menu at the top jumps to each part.

## Not covered, and why

- **Safari and WebKit:** Tab skips links there unless the user turns on "Press Tab to
  highlight each item", so a WebKit Tab walk tests a setting, not the site. The axe gate and
  the no-JavaScript finder tests do run in WebKit.
- **Screen readers:** this pass is keyboard behaviour, not a screen-reader audit. NVDA and
  VoiceOver passes belong with M2's consent flows, which are the most demanding forms.
- **The interactive islands** (passkey sign-up, the reconnect dialog): not checked. They're
  outside the finder and the M1 console flows.
- **Zoom and reflow at 400%:** not part of this pass.
