# docfx

The documentation site, published at https://retrocorelabs.github.io/HdlcGatewayTest/ by
`.github/workflows/docs.yml` on every push to main.

Build it locally, from `src`:

```
docfx docfx/docfx.json --warningsAsErrors
```

The site lands in `docfx/_site` (not tracked). It must end with 0 warnings: a warning is a
broken link or a comment DocFX could not read.

| File | What it is |
|---|---|
| `docfx.json` | Which projects the API reference comes from (the six program projects, not the tests), which READMEs are pages, the screenshots, the icon |
| `toc.yml` | The header menu: Home, Overview, The window, Command line, API Reference, GitHub |
| `index.md` | The home page |
| `api/index.md` | The API reference start page; the rest of `api/` is generated |
| `filterConfig.yml` | Hides the members the window classes inherit from Avalonia |
| `template/public/main.css` | Header logo size, menu on one line, text contrast in light and dark |
| `icon.svg` | The header and tab icon |

A README under `src/` is mapped from `src`, the repository's own `README.md` and `.github`
files from the repository root. A link from the root README to a README under `src/` cannot be
matched across those two groups by DocFX, so those links point at github.com.
