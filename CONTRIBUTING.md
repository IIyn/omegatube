# Contributing to OmegaTube

Thanks for helping! Bug reports, ideas and pull requests are all welcome.

## Using AI / LLMs

**Code written with the help of an LLM (ChatGPT, Claude, Copilot…) is accepted**, on three conditions:

1. **You reviewed it yourself.** You read every line you submit, understand what it does and can answer questions about it in the review. "The AI wrote it" is not an explanation; the code is yours once you open the pull request.
2. **You tested it.** On a TV (say which model and Tizen version) or at least on the emulator or with the PC tools. Untested code, AI-written or not, won't be merged.
3. **You show it working, when you can.** A photo, screenshot or short video of the change on the TV (before / after for a bug fix) makes the review much faster.

Please say in the pull request that you used an LLM, and for what. It's not held against you; it tells the reviewer where to look more closely. LLMs on this project often get Tizen-specific details wrong (APIs that don't exist on a specific Tizen version, privileges, native window handling), so double-check those parts.

## Reporting a bug or suggesting a feature

- **Bug**: use the *Report a problem* screen in the app (Settings → About), or the [bug report form](https://github.com/IIyn/omegatube/issues/new?template=bug.yml).
- **Idea or change**: use the [feature request form](https://github.com/IIyn/omegatube/issues/new?template=feature.yml). One idea per issue; if it already exists, add a 👍 or a comment there.

## Pull requests

1. For anything bigger than a small fix, open or comment on an [issue](https://github.com/IIyn/omegatube/issues) first, so we agree on the approach before you spend time on it.
2. Fork the repository, create a branch from `main` (e.g. `fix/12-subtitles-offset`).
3. **One topic per pull request.** Two unrelated fixes go in two pull requests: they can then be tested, discussed and merged separately.
4. Open the pull request and fill in the template: what it changes, the issue it fixes (`Fixes #12`), how you tested it, and screenshots if any.

### Code guidelines

- Follow the style of the surrounding code. Comments explain *why*, not *what*.
- Reuse what exists (e.g. `T("…")` for texts, static methods etc...) rather than adding a second way to do the same thing.
- Texts shown in the app are written in English through `T("…")`; add the French and Spanish translations in `app/OmegaTube/I18n.cs` if you can (otherwise say so and they'll be added).
- The app targets **Tizen 5.5 (API 6, 2020 TVs)** and must keep working there, even if you test on a newer TV. Compile against the oldest TizenFX: APIs missing on 5.5 (no `CornerRadius`, no blur) fail the build or go through reflection.
- `remote-config.json` is downloaded by every installed copy of the app: changes to it must stay compatible with released versions.
- Never commit certificates, account tokens, `res/dev.json` or logs with personal data.

Build and test instructions are in the [README's Development section](README.md#development).

### Automatic checks

Every pull request runs the [CI workflow](.github/workflows/ci.yml) on GitHub Actions:

| Check | What it does |
| --- | --- |
| Checks | `VERSION` format, valid JSON (`remote-config.json`), valid YAML (issue templates, workflows), bug report fields in sync with `BugReport.cs`, shell scripts (`bash -n`, `shellcheck`). |
| Core + PC tools | Builds the C++ core with `ytcheck` and `mp4dump` natively. |
| TV package | Runs the full build (Docker toolchain + .NET) and attaches the `.tpk` to the run, under *Artifacts*. |

All three must pass before a merge. On a first contribution, GitHub asks the maintainer to approve the run before it starts.

### Testing a pull request on a TV

Download the `omegatube-pr<number>` artifact from the pull request's *Checks* tab, unzip it, then sign it for your TV and install it:

```sh
TPK=$(ls org.omegatube.app-*.tpk)
~/tizen-studio/tools/ide/bin/tizen package -t tpk -s omegatube -- "$TPK"
~/tizen-studio/tools/ide/bin/tizen install -n "$TPK" -t <device> -- .   # device name: 3rd column of `sdb devices`
```

Or check out the branch (`gh pr checkout <number>`) and use `scripts/build-app.sh` + `scripts/deploy.sh` as usual.

### Review

The maintainer is asked to review automatically (`.github/CODEOWNERS`). Answer comments by pushing new commits to the same branch; the checks run again. Once approved and green, the pull request is merged with *Squash and merge*.

## License

By contributing, you agree that your contribution is released under the project's [GNU GPL v3](LICENSE) license.
