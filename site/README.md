# fdull.com

A single static landing page. No framework, package manager, dependencies, build
step, external fonts or analytics. Serve `public/` with any static host, or
open `public/index.html` directly. For a local HTTP preview:

```sh
python3 -m http.server 8080 --bind 127.0.0.1 --directory site/public
```

Run that command from the repository root. Open http://127.0.0.1:8080/.

## Files

- `public/index.html`: all copy, the 69-rule guide with a before/after example for every
  rule, installation instructions and small
  inline JavaScript enhancements (search, family filters, rule permalinks and copy
  buttons). Content and native disclosure controls work without JavaScript.
- `public/styles.css`: responsive layout, system fonts, keyboard focus and reduced-motion
  support.
- `public/coverage.json`: unmodified output of the project's `fdull coverage` command.
- `public/og.png`: social share card. Page metadata targets the intended fdull.com domain.
- `public/LICENSE.txt`: the project's MIT license.
- `.openai/hosting.json`: private Sites preview identity; not required by a static
  host. Sites receives a generated, dependency-free Worker wrapper for the same
  static files; that wrapper is deployment output, not a frontend build system.

## Automatic deployment from GitHub

The source repository is https://github.com/wonderlydotcom/fdull. Configure a
Cloudflare Pages project in the personal account that owns `fdull.com`, using
Cloudflare's native GitHub integration. Pushes to `main` publish the website;
pull requests can receive preview deployments.

Use these project settings:

| Setting | Value |
| --- | --- |
| Repository | `wonderlydotcom/fdull` |
| Production branch | `main` |
| Framework preset | None |
| Root directory | `site` |
| Build command | Empty (no build step) |
| Build output directory | `public` |

Connect with **Compute > Workers & Pages > Create application > Pages > Import
an existing Git repository**. Authorize Cloudflare's GitHub app to access this
repository, select it, apply the settings above, and choose **Save and Deploy**.
Then add `fdull.com` under **Custom domains > Set up a domain** and complete the
DNS/certificate setup in the same account.

Only `site/public` is served: HTML, CSS, the rule coverage inventory, the social
image and the license. The .NET projects, NuGet packages, development docs,
credentials and private Sites preview metadata are outside that directory.
There are no GitHub Actions secrets or hand-uploaded ZIP files to maintain.

After editing the website, commit and push normally. Cloudflare reports the
deployment result on the GitHub commit and in the Pages dashboard. Confirm the
deployed commit and check https://fdull.com after the first successful deployment.
Later releases of the NuGet packages are a separate publishing operation.

References: [Git integration](https://developers.cloudflare.com/pages/get-started/git-integration/)
and [Custom domains](https://developers.cloudflare.com/pages/configuration/custom-domains/).

The Wonderly engineering post remains marked **Coming soon** until its published
URL is supplied. The existing private Sites preview is separate from the GitHub
deployment; its local metadata is preserved and ignored by Git.

## Content maintenance

The current content follows the root README, `src/FDull/SafetyRules.fs`,
`docs/fsharp-safety-standard.md`, relevant analyzer bodies and regression fixtures.
The page deliberately distinguishes the standard's 69 catalog entries from full
implementation. BUILD006 is external; per-rule remaining work lives in the linked
coverage inventory. Examples are focused excerpts, not complete applications or
standalone acceptance fixtures. Named application helpers illustrate reviewed
contracts and are not FDull APIs; configuration sketches are labeled explicitly.
When rules change, review the descriptions, rationales and both code examples,
then refresh the inventory from the built CLI:

```sh
dotnet src/FDull.Tool/bin/Release/net10.0/FDull.Cli.dll coverage > site/public/coverage.json
```

The repository guard checks the compiled F# projects; it is not a verifier for
this page's browser JavaScript. No guard permissions or fingerprints were changed
for the site. Validate the page separately, and run the repository's required
formatting, Release build, tests and CLI verification checks.

## Social card

`og.png` was generated with the built-in image generation tool. Final prompt:

> Create one finished landscape social share card, 1536 x 1024, for a playful
> developer tool landing page named fdull. Warm ivory background, charcoal ink,
> warm orange accent. Editorial pocket field guide, confident clean typography,
> generous margins, flat print style. Exact text: “fdull”; “BY WONDERLY”; “F# for
> the rest of us.”; “Less clever. More clear.”; “F# with fewer sharp edges.”;
> “fdull.com”. On the right, a slightly tilted orange rounded square with two small
> dark oval eyes and a calm flat mouth. No browser chrome, arrows or badges.
