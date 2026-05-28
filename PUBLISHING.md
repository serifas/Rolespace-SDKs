# Publishing the Rolespace SDKs

The three SDKs share one repo. Each ships independently, gated by a language-prefixed
git tag. Every release goes through GitHub Actions — the workflows live in
[`.github/workflows`](.github/workflows).

## Repo layout

```
sdks/
├── .github/workflows/
│   ├── ci.yml                  ← runs on every PR (smoke + pack dry-run)
│   ├── publish-node.yml        ← fires on tags `node-v*`
│   ├── publish-python.yml      ← fires on tags `py-v*`
│   └── publish-csharp.yml      ← fires on tags `cs-v*`
├── node/        package.json   → npmjs.com/package/rolespace
├── python/      pyproject.toml → pypi.org/project/rolespace
└── csharp/      *.csproj       → nuget.org/packages/Rolespace.Sdk
```

## One-time setup

These secrets and accounts need to exist before the first release. If the repo
will live somewhere other than `github.com/rolespace/rolespace-sdks`, find-and-replace
that string across the package files and workflows first.

### npm

1. Create an npm account and the `rolespace` package name (`npm publish` the first
   version manually, OR reserve the name with `npm access`).
2. Create an automation token: npmjs.com → Profile → Access Tokens →
   **Automation** scope.
3. In the GitHub repo: **Settings → Secrets and variables → Actions → New repository
   secret** named `NPM_TOKEN`.

### PyPI (trusted publisher — recommended)

No long-lived token needed; PyPI authenticates the GitHub Actions run via OIDC.

1. Reserve the project name on test.pypi.org or pypi.org (one option: upload v0.1.0
   manually with `twine` the first time, then enable trusted publisher for all
   subsequent releases).
2. On pypi.org → your project → **Publishing** → **Add a new pending publisher**:
   - Owner: `rolespace`
   - Repository name: `rolespace-sdks`
   - Workflow name: `publish-python.yml`
   - Environment name: `pypi`
3. In the GitHub repo: **Settings → Environments → New environment → `pypi`**.
   No secrets needed in the environment — OIDC handles auth.

If you'd rather use a classic token, add `PYPI_API_TOKEN` as a repo secret and
swap the `pypa/gh-action-pypi-publish` step to use `password: ${{ secrets.PYPI_API_TOKEN }}`.

### NuGet

1. Create an account on nuget.org and reserve the `Rolespace.Sdk` package id (upload
   v0.1.0 manually the first time, or use the package reservation form).
2. Account → API keys → **Create**:
   - Scopes: **Push** + **Push new packages and package versions**
   - Glob pattern: `Rolespace.*`
3. In the GitHub repo: add secret `NUGET_API_KEY`.

## Cutting a release

The version that actually ships is read from each language's manifest, NOT from
the git tag. The tag is just a trigger and a sanity check.

### Node

```bash
# 1. Bump the version in node/package.json
vim node/package.json          # change "version": "0.1.0" → "0.1.1"
git commit -am "node: 0.1.1"

# 2. Tag matching the new version
git tag node-v0.1.1
git push origin main node-v0.1.1
```

`publish-node.yml` runs, asserts the tag matches `package.json`, then runs
`npm publish --provenance --access public`. Within a minute it's live on npm.

### Python

```bash
vim python/pyproject.toml      # change version = "0.1.0" → "0.1.1"
git commit -am "python: 0.1.1"
git tag py-v0.1.1
git push origin main py-v0.1.1
```

The workflow builds `sdist` + `wheel`, uploads them as a workflow artifact, then
the `publish` job uses PyPI OIDC to push them.

### C#

```bash
vim csharp/Rolespace.Sdk.csproj   # change <Version>0.1.0</Version> → <Version>0.1.1</Version>
git commit -am "csharp: 0.1.1"
git tag cs-v0.1.1
git push origin main cs-v0.1.1
```

The workflow builds, packs `.nupkg` + `.snupkg` (symbols), and pushes both to
nuget.org with `--skip-duplicate` so re-runs are safe.

## Bootstrapping the first release

For each registry, the first version usually needs to be pushed manually so the
package name is claimed. From that point on the workflows take over.

```bash
# npm
cd node && npm pack && npm publish --access public

# PyPI (one-time, before enabling trusted publisher)
cd python && python -m pip install build twine
python -m build
python -m twine upload dist/*

# NuGet
cd csharp && dotnet pack -c Release
dotnet nuget push 'bin/Release/*.nupkg' \
    --api-key "$NUGET_API_KEY" \
    --source 'https://api.nuget.org/v3/index.json'
```

## Versioning

[Semver](https://semver.org/) across the three SDKs, but they can drift —
they don't need to share a number. A change to `Rolespace.Sdk` doesn't oblige a
Python bump.

Pre-1.0 (where we are): any minor bump can break compat. After 1.0, MAJOR for
breaking, MINOR for additive, PATCH for fixes.

## Pulling a release

If you push a bad release:

- **npm**: `npm deprecate rolespace@x.y.z "use x.y.(z+1)"`. Genuine unpublish
  only works within 72 hours and is discouraged.
- **PyPI**: yank with `pypi.org → release → Options → Yank` (keeps the file but
  hides it from new installs).
- **NuGet**: unlist with `nuget.org → package → Manage → Unlist`.

In all three cases: bump the version, fix the bug, push a new tag.
