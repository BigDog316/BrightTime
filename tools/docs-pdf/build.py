"""Builds BrightTime's documentation as PDFs: python build.py <repo> <out-dir>.

Each book: a cover, a contents page, then its chapters (Markdown files). The body is rendered by
Edge through Playwright with the heading outline embedded; the outline gives every heading's page,
from which the contents page and the running headers are made. PyMuPDF merges the parts, draws the
headers and footers, turns the contents into links, and writes the bookmarks.
"""
import html
import os
import re
import sys
import tempfile
import unicodedata
from datetime import date

import markdown
import pymupdf
from playwright.sync_api import sync_playwright

REPO, OUT = sys.argv[1], sys.argv[2]
GITHUB = "https://github.com/BigDog316/BrightTime"
EDITION = "As built through decision D66 \u00b7 22 October 2026"
FONTS = "C:/Windows/Fonts"

NAVY = "#17324d"
AMBER = "#f2a516"

BOOKS = [
    {
        "file": "BrightTime-Guide-for-Families.pdf",
        "title": "Guide for Families",
        "subtitle": "Finding activities, your account, and your children\u2019s profiles and consent",
        "audience": "For parents and guardians",
        "chapters": [("docs/user/families.md", None), ("docs/user/README.md", "About BrightTime")],
    },
    {
        "file": "BrightTime-Guide-for-Young-People.pdf",
        "title": "Guide for Young People",
        "subtitle": "Your own account, finding things to do, and staying safe",
        "audience": "For anyone 13 or over with their own account",
        "chapters": [("docs/user/young-people.md", None), ("docs/user/README.md", "About BrightTime")],
    },
    {
        "file": "BrightTime-Guide-for-Organisations.pdf",
        "title": "Guide for Organisations",
        "subtitle": "Listings, registration, attendance, messages and reports in the console",
        "audience": "For owners, admins and staff of organisations that run activities",
        "chapters": [("docs/user/providers.md", None), ("docs/user/README.md", "About BrightTime")],
    },
    {
        "file": "BrightTime-Guide-for-Network-Organisers.pdf",
        "title": "Guide for Network Organisers",
        "subtitle": "Vetting, review, taxonomy, wording and the audit log",
        "audience": "For the county\u2019s network organisers",
        "chapters": [("docs/user/organisers.md", None), ("docs/user/README.md", "About BrightTime")],
    },
    {
        "file": "BrightTime-Technical-Reference.pdf",
        "title": "Technical Reference",
        "subtitle": "Architecture, data and security, the API, configuration, operations and development",
        "audience": "For the people who build and run BrightTime",
        "chapters": [
            ("docs/technical/README.md", "Overview"),
            ("docs/technical/architecture.md", None),
            ("docs/technical/data-and-security.md", None),
            ("docs/technical/api.md", None),
            ("docs/technical/configuration.md", None),
            ("docs/technical/operations.md", None),
            ("docs/technical/development.md", None),
        ],
    },
]

LOGO = f"""<svg viewBox="0 0 64 64" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
  <g stroke="{AMBER}" stroke-width="4" stroke-linecap="round">
    <line x1="32" y1="4" x2="32" y2="12"/><line x1="32" y1="52" x2="32" y2="60"/>
    <line x1="4" y1="32" x2="12" y2="32"/><line x1="52" y1="32" x2="60" y2="32"/>
    <line x1="12.2" y1="12.2" x2="17.9" y2="17.9"/><line x1="46.1" y1="46.1" x2="51.8" y2="51.8"/>
    <line x1="12.2" y1="51.8" x2="17.9" y2="46.1"/><line x1="46.1" y1="17.9" x2="51.8" y2="12.2"/>
  </g>
  <circle cx="32" cy="32" r="14" fill="{AMBER}"/>
</svg>"""

BASE_CSS = f"""
@page {{ size: Letter; margin: 0.95in 0.85in 0.9in 0.85in; }}
html {{ -webkit-print-color-adjust: exact; print-color-adjust: exact; }}
body {{ font-family: "Segoe UI", system-ui, sans-serif; font-size: 10.2pt; line-height: 1.5;
       color: #1f2a37; margin: 0; }}
section.chapter {{ break-before: page; }}
section.chapter:first-of-type {{ break-before: auto; }}
.eyebrow {{ font-size: 9pt; font-weight: 600; letter-spacing: .14em; text-transform: uppercase;
           color: #b7791f; margin: 0 0 4pt; }}
h1 {{ font-size: 25pt; line-height: 1.15; color: {NAVY}; font-weight: 600; margin: 0 0 6pt;
     padding-bottom: 10pt; border-bottom: 3pt solid {AMBER}; }}
h1 + p, h1 + .lead {{ margin-top: 12pt; }}
h2 {{ font-size: 15pt; color: {NAVY}; font-weight: 600; margin: 22pt 0 6pt; padding-top: 8pt;
     border-top: 0.75pt solid #d5dde6; break-after: avoid; }}
h3 {{ font-size: 11.5pt; color: {NAVY}; font-weight: 600; margin: 14pt 0 4pt; break-after: avoid; }}
h4 {{ font-size: 10.5pt; margin: 10pt 0 2pt; break-after: avoid; }}
p {{ margin: 0 0 7pt; orphans: 3; widows: 3; }}
ul, ol {{ margin: 0 0 8pt; padding-left: 18pt; }}
li {{ margin: 0 0 3pt; }}
li > ul, li > ol {{ margin-top: 3pt; }}
a {{ color: #1d5fa3; text-decoration: none; }}
strong {{ font-weight: 600; color: #111b27; }}
code {{ font-family: "Cascadia Mono", Consolas, monospace; font-size: 8.6pt; background: #eef2f6;
       border-radius: 3pt; padding: 0.5pt 3pt; color: #23364d; }}
pre {{ background: #f5f7fa; border: 0.75pt solid #dbe2ea; border-left: 3pt solid {NAVY};
      border-radius: 3pt; padding: 7pt 9pt; margin: 4pt 0 10pt; white-space: pre-wrap;
      font-size: 8.2pt; line-height: 1.38;
      overflow-wrap: anywhere; break-inside: avoid; }}
pre code {{ background: none; padding: 0; font-size: 8.2pt; line-height: 1.4; color: #1f2a37; }}
pre.diagram code {{ font-size: 7.4pt; white-space: pre; }}
table {{ width: 100%; border-collapse: collapse; margin: 6pt 0 12pt; font-size: 8.9pt;
        line-height: 1.38; }}
thead th {{ background: {NAVY}; color: #fff; font-weight: 600; text-align: left;
           padding: 4.5pt 6pt; vertical-align: bottom; }}
td {{ padding: 4pt 6pt; border-bottom: 0.6pt solid #dbe2ea; vertical-align: top; }}
tbody tr:nth-child(even) td {{ background: #f6f8fb; }}
tr {{ break-inside: avoid; }}
table.keep {{ break-inside: avoid; }}
p:has(+ table), p:has(+ ul), p:has(+ ol), p:has(+ pre), p:has(+ .figure) {{ break-after: avoid; }}
li:last-child {{ break-before: avoid; }}
li {{ break-inside: avoid; }}
td code {{ font-size: 8pt; overflow-wrap: break-word; }}
.figure {{ margin: 4pt 0 12pt; break-inside: avoid; }}
.figure svg {{ width: 100%; height: auto; display: block; }}
th[style*="center"], td[style*="center"] {{ text-align: center; }}
blockquote {{ margin: 6pt 0 10pt; padding: 6pt 10pt; background: #fff8e6;
             border-left: 3pt solid {AMBER}; }}
hr {{ border: 0; border-top: 0.75pt solid #d5dde6; margin: 14pt 0; }}
"""


def slug(text: str) -> str:
    text = unicodedata.normalize("NFKD", text).encode("ascii", "ignore").decode()
    text = re.sub(r"[^\w\s-]", "", text).strip().lower()
    return re.sub(r"[-\s]+", "-", text)


def prefix_for(path: str) -> str:
    return slug(os.path.splitext(os.path.basename(os.path.dirname(path)))[0] + "-" + os.path.splitext(os.path.basename(path))[0])


def rewrite_links(md: str, path: str, chapter_paths: dict[str, str]) -> str:
    here = os.path.dirname(path)

    def fix(m: re.Match) -> str:
        text, target = m.group(1), m.group(2)
        if target.startswith(("http://", "https://", "mailto:")):
            return m.group(0)
        file_part, _, frag = target.partition("#")
        resolved = os.path.normpath(os.path.join(here, file_part)).replace("\\", "/") if file_part else path
        if resolved in chapter_paths:
            pre = chapter_paths[resolved]
            return f"[{text}](#{pre}--{frag})" if frag else f"[{text}](#{pre})"
        kind = "tree" if os.path.isdir(os.path.join(REPO, resolved)) else "blob"
        url = f"{GITHUB}/{kind}/main/{resolved}" + (f"#{frag}" if frag else "")
        return f"[{text}]({url})"

    return re.sub(r"(?<!!)\[([^\]]+)\]\(([^)\s]+)\)", fix, md)


def strip_local_contents(md: str) -> str:
    """Drops a Markdown file's own contents list: the PDF has a real one."""
    lines, out, i = md.split("\n"), [], 0
    while i < len(lines):
        j = i
        while j < len(lines) and re.fullmatch(r"- \[[^\]]+\]\(#[^)]+\)", lines[j].strip()):
            j += 1
        if j - i >= 2:
            i = j
            while i < len(lines) and lines[i].strip() == "" and out and out[-1].strip() == "":
                i += 1
            continue
        out.append(lines[i])
        i += 1
    return "\n".join(out)


def normalise(md: str) -> str:
    """GitHub-flavoured habits Python-Markdown doesn't share: a list straight after a paragraph,
    two-space nesting, and fences indented inside list items."""
    item = re.compile(r"(\d+\.|[-*])\s")
    out: list[str] = []
    fence_indent = None
    gap = loose = False
    for line in md.split(chr(10)):
        stripped = line.lstrip(" ")
        indent = len(line) - len(stripped)
        if stripped.startswith("```"):
            if fence_indent is None:
                fence_indent = indent
                if out and out[-1].strip():
                    out.append("")
                out.append(stripped)
            else:
                fence_indent = None
                out.append(stripped)
                out.append("")
            continue
        if fence_indent is not None:
            out.append(line[fence_indent:] if line[:fence_indent].strip() == "" else line)
            continue
        # A list item holding its own paragraph (after a blank line) makes the list loose; the
        # next item then needs a blank line too, or it is read as part of that paragraph.
        if not stripped:
            gap = True
        elif indent and gap and out and not item.match(stripped):
            loose = True
        if stripped and not indent and not item.match(stripped):
            loose = False
        if stripped:
            gap = False
        if item.match(stripped) and indent == 0 and out and out[-1].strip() \
                and not item.match(out[-1].lstrip(" ")) and not out[-1].startswith(("|", "#")) \
                and (not out[-1].startswith(" ") or loose):
            out.append("")
        if indent and not stripped.startswith("|"):
            line = " " * (indent * 2) + stripped
        out.append(line)
    return chr(10).join(out)


def break_code(body: str) -> str:
    """Lets inline code wrap after a separator rather than mid-word."""
    parts = re.split(r"(<pre[^>]*>.*?</pre>)", body, flags=re.S)

    def soften(m: re.Match) -> str:
        return m.group(1) + re.sub(r"([/:._{(,=])(?=[^<\s])", r"\1<wbr>", m.group(2)) + "</code>"

    return "".join(p if p.startswith("<pre") else re.sub(r"(<code>)(.*?)</code>", soften, p, flags=re.S) for p in parts)


def chapter_html(path: str, title_override: str | None, number: int, chapter_paths: dict[str, str]) -> str:
    md = open(os.path.join(REPO, path), encoding="utf-8").read()
    md = strip_local_contents(md)
    md = normalise(md)
    md = rewrite_links(md, path, chapter_paths)
    if title_override:
        md = re.sub(r"\A# .*", f"# {title_override}", md, count=1)
    pre = chapter_paths[path]
    converter = markdown.Markdown(
        extensions=["tables", "fenced_code", "sane_lists", "toc"],
        extension_configs={"toc": {"slugify": lambda value, sep: f"{pre}--{slug(value)}", "permalink": False}},
    )
    body = converter.convert(md)
    # Box-drawing diagrams are redrawn as figures for print.
    figure = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "hosts.svg"), encoding="utf-8").read()
    body = re.sub(r"<pre><code>(?:(?!</code>).)*[\u2500-\u257f](?:(?!</code>).)*</code></pre>",
                  lambda m: f'<div class="figure">{figure}</div>', body, flags=re.S)
    body = break_code(body)
    # A short table stays on one page; a long one breaks, repeating its header.
    body = re.sub(r"<table>(.*?)</table>",
                  lambda m: ('<table class="keep">' if m.group(1).count("<tr") <= 14 else "<table>") + m.group(1) + "</table>",
                  body, flags=re.S)
    eyebrow = f'<p class="eyebrow">Chapter {number}</p>'
    body = re.sub(r"(<h1[^>]*>)", eyebrow + r"\1", body, count=1)
    return f'<section class="chapter" id="{pre}">{body}</section>'


def cover_html(book: dict) -> str:
    return f"""<!doctype html><html><head><meta charset="utf-8"><style>
@page {{ size: Letter; margin: 0; }}
html {{ -webkit-print-color-adjust: exact; print-color-adjust: exact; }}
body {{ margin: 0; font-family: "Segoe UI", system-ui, sans-serif; color: #1f2a37; }}
.page {{ width: 8.5in; height: 11in; position: relative; overflow: hidden; }}
.band {{ position: absolute; inset: 0 0 auto 0; height: 7.1in;
        background: linear-gradient(155deg, #1d3d5e 0%, {NAVY} 55%, #0f2236 100%); }}
.glow {{ position: absolute; right: -1.6in; top: -1.4in; width: 5.6in; height: 5.6in; border-radius: 50%;
        background: radial-gradient(circle, rgba(242,165,22,.34) 0%, rgba(242,165,22,0) 68%); }}
.brand {{ position: absolute; left: 0.9in; top: 0.85in; display: flex; align-items: center; gap: 12pt;
         color: #fff; font-size: 17pt; font-weight: 600; letter-spacing: .01em; }}
.brand svg {{ width: 34pt; height: 34pt; }}
.titles {{ position: absolute; left: 0.9in; right: 0.9in; bottom: 4.45in; color: #fff; }}
.kicker {{ font-size: 10pt; letter-spacing: .2em; text-transform: uppercase; color: {AMBER};
          font-weight: 600; margin-bottom: 12pt; }}
h1 {{ font-size: 40pt; line-height: 1.08; margin: 0 0 16pt; font-weight: 600; }}
.subtitle {{ font-size: 14pt; line-height: 1.4; color: #d6e2ee; max-width: 5.6in; }}
.rule {{ position: absolute; left: 0; right: 0; top: 7.1in; height: 6pt; background: {AMBER}; }}
.lower {{ position: absolute; left: 0.9in; right: 0.9in; top: 7.75in; }}
.audience {{ font-size: 13pt; color: {NAVY}; font-weight: 600; margin-bottom: 8pt; }}
.about {{ font-size: 10pt; line-height: 1.55; color: #4a5a6b; max-width: 5.8in; }}
.meta {{ position: absolute; left: 0.9in; right: 0.9in; bottom: 0.75in; font-size: 8.5pt; color: #6b7a8a;
        display: flex; justify-content: space-between; border-top: 0.75pt solid #d5dde6; padding-top: 8pt; }}
</style></head><body><div class="page">
<div class="band"></div><div class="glow"></div>
<div class="brand">{LOGO}<span>BrightTime</span></div>
<div class="titles"><div class="kicker">Documentation</div><h1>{html.escape(book['title'])}</h1>
<div class="subtitle">{html.escape(book['subtitle'])}</div></div>
<div class="rule"></div>
<div class="lower"><div class="audience">{html.escape(book['audience'])}</div>
<div class="about">BrightTime is a countywide platform for out-of-school time. Families and young people find
after-school, summer and weekend activities; the organisations that run them take registrations and
record attendance; and the county's network organisers vet every organisation and listing.</div></div>
<div class="meta"><span>{EDITION}</span><span>Kettle County, NY (demonstration region)</span></div>
</div></body></html>"""


def contents_html(book: dict, entries: list[tuple[int, str, int]]) -> str:
    rows = []
    for level, title, page in entries:
        cls = "c1" if level == 1 else "c2"
        rows.append(f'<a class="row {cls}" href="https://toc.invalid/{page}"><span class="t">{html.escape(title)}</span>'
                    f'<span class="dots"></span><span class="n">{page}</span></a>')
    return f"""<!doctype html><html><head><meta charset="utf-8"><style>
{BASE_CSS}
h1 {{ margin-bottom: 16pt; }}
.row {{ display: flex; align-items: baseline; color: #1f2a37; text-decoration: none; break-inside: avoid; }}
.c1 {{ font-weight: 600; font-size: 10.5pt; color: {NAVY}; margin-top: 8pt; padding-bottom: 1pt; }}
.c2 {{ font-size: 9.2pt; line-height: 1.32; padding: 0.6pt 0 0.6pt 14pt; }}
.dots {{ flex: 1; border-bottom: 1pt dotted #9aa8b6; margin: 0 6pt; transform: translateY(-3pt); }}
.n {{ font-variant-numeric: tabular-nums; min-width: 18pt; text-align: right; }}
</style></head><body><h1>Contents</h1>{''.join(rows)}</body></html>"""


def body_html(book: dict) -> str:
    chapter_paths = {p: prefix_for(p) for p, _ in book["chapters"]}
    parts = [chapter_html(p, t, i + 1, chapter_paths) for i, (p, t) in enumerate(book["chapters"])]
    return f"""<!doctype html><html><head><meta charset="utf-8"><title>{html.escape(book['title'])}</title>
<style>{BASE_CSS}</style></head><body>{''.join(parts)}</body></html>"""


def render(page, markup: str, path: str, outline: bool = False) -> None:
    page.set_content(markup, wait_until="load")
    page.pdf(path=path, format="Letter", print_background=True, prefer_css_page_size=True, outline=outline, tagged=True)


def draw_chrome(doc: pymupdf.Document, book: dict, chapters: list[tuple[int, str]], first_body: int) -> None:
    regular, semibold = f"{FONTS}/segoeui.ttf", f"{FONTS}/seguisb.ttf"
    font_r, font_b = pymupdf.Font(fontfile=regular), pymupdf.Font(fontfile=semibold)
    grey, navy = (0.42, 0.48, 0.54), (0.09, 0.20, 0.30)
    for index in range(1, len(doc)):
        page = doc[index]
        w, h = page.rect.width, page.rect.height
        left, right = 0.85 * 72, w - 0.85 * 72
        page.insert_font(fontname="segr", fontfile=regular)
        page.insert_font(fontname="segb", fontfile=semibold)
        if index >= first_body:
            current = next((title for start, title in reversed(chapters) if start <= index), "")
            title = f"BrightTime \u00b7 {book['title']}"
            page.insert_text((left, 0.55 * 72), title, fontname="segb", fontsize=7.8, color=navy)
            size = 7.8
            page.insert_text((right - font_r.text_length(current, size), 0.55 * 72), current,
                             fontname="segr", fontsize=size, color=grey)
            page.draw_line((left, 0.62 * 72), (right, 0.62 * 72), color=(0.84, 0.87, 0.9), width=0.6)
        number = str(index + 1)
        page.insert_text((left, h - 0.5 * 72), EDITION, fontname="segr", fontsize=7.5, color=grey)
        page.insert_text((right - font_b.text_length(number, 8.5), h - 0.5 * 72), number,
                         fontname="segb", fontsize=8.5, color=navy)


def build(book: dict, browser) -> str:
    tmp = os.path.join(os.path.dirname(os.path.abspath(__file__)), "work", os.path.splitext(book["file"])[0])
    os.makedirs(tmp, exist_ok=True)
    if True:
        page = browser.new_page()
        cover_pdf, body_pdf, toc_pdf = (os.path.join(tmp, n) for n in ("cover.pdf", "body.pdf", "toc.pdf"))
        render(page, cover_html(book), cover_pdf)
        render(page, body_html(book), body_pdf, outline=True)
        body = pymupdf.open(body_pdf)
        outline = [(lvl, title, pg) for lvl, title, pg, *_ in body.get_toc(simple=False) if lvl <= 2]

        # The contents page's length sets every page number after it; settle it.
        toc_pages = 1
        for _ in range(4):
            offset = 1 + toc_pages
            entries = [(lvl, title, pg + offset) for lvl, title, pg in outline]
            render(page, contents_html(book, entries), toc_pdf)
            actual = pymupdf.open(toc_pdf).page_count
            if actual == toc_pages:
                break
            toc_pages = actual
        page.close()

        doc = pymupdf.open()
        doc.insert_pdf(pymupdf.open(cover_pdf))
        toc = pymupdf.open(toc_pdf)
        doc.insert_pdf(toc)
        doc.insert_pdf(body)
        offset = 1 + toc.page_count

        for index in range(1, offset):
            p = doc[index]
            for link in p.get_links():
                if link.get("kind") == pymupdf.LINK_URI and link.get("uri", "").startswith("https://toc.invalid/"):
                    target = int(link["uri"].rsplit("/", 1)[1]) - 1
                    p.delete_link(link)
                    p.insert_link({"kind": pymupdf.LINK_GOTO, "page": target, "from": link["from"],
                                   "to": pymupdf.Point(0, 0), "zoom": 0})

        chapters = [(pg - 1 + offset, title) for lvl, title, pg in outline if lvl == 1]
        draw_chrome(doc, book, chapters, offset)

        full = [(lvl, title, pg + offset) for lvl, title, pg, *_ in body.get_toc(simple=False) if lvl <= 3]
        doc.set_toc([[1, "Contents", 2]] + [[lvl, title, pg] for lvl, title, pg in full])
        doc.set_metadata({"title": f"BrightTime \u2014 {book['title']}", "author": "BrightTime",
                          "subject": book["subtitle"], "keywords": "BrightTime, documentation",
                          "creator": "BrightTime documentation build", "producer": "PyMuPDF"})
        target = os.path.join(OUT, book["file"])
        doc.subset_fonts()
        doc.save(target, garbage=4, deflate=True)
        return target


def main() -> None:
    os.makedirs(OUT, exist_ok=True)
    only = sys.argv[3:]
    with sync_playwright() as p:
        browser = p.chromium.launch(channel="msedge")
        for book in BOOKS:
            if only and not any(o in book["file"] for o in only):
                continue
            path = build(book, browser)
            print(f"{os.path.basename(path)}: {pymupdf.open(path).page_count} pages")
        browser.close()


if __name__ == "__main__":
    main()
