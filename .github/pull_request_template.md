<!-- Thanks for contributing! A few lines here make the review much faster. See CONTRIBUTING.md. -->

## What does it change?

<!-- What the PR does and why. Link the issue it fixes: "Fixes #123". -->

## How was it tested?

- [ ] On a TV: model `…`, Tizen `…`
- [ ] On the emulator (`scripts/emulator.sh`)
- [ ] With the PC tools (`ytcheck`, `mp4dump`)
- [ ] Not tested on a device (say why)

## Screenshots / video

<!-- A photo, screenshot or short video of the change on the TV (before / after for a bug fix). -->

## Checklist

- [ ] One topic per PR (unrelated fixes go in separate PRs)
- [ ] If an LLM helped write the code: I reviewed every line myself and say in the description what it was used for
- [ ] New texts shown in the app go through `T("…")`, with French and Spanish added in `app/OmegaTube/I18n.cs` (or say so and they'll be added)
- [ ] `remote-config.json` changes are still valid for the released versions of the app
- [ ] No tokens, certificates, logs with personal data or `res/dev.json` committed
