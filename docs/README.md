# docs

| Path | Purpose |
|---|---|
| `banner.png`, `logo.png` | Brand images used by the README |
| `screenshots/` | Real screenshots of the app (one per tab) |
| `NOTES.ar.txt` | Detailed Arabic developer/user notes (formerly `README.txt`) |

## Screenshots

Screenshots are captured from the real running app by `scripts/capture-screenshots.ps1`, which the
GitHub Actions workflow runs on every build.

The workflow commits them to `docs/screenshots/` automatically after each build on the branch, so the README shows them
after the first successful run (refresh the page after the run finishes).
They are also attached to every run as the `screenshots` artifact.
Expected names: `01-start-here`, `02-chat`, `03-library`, `04-tools`, `05-settings`, `06-log`, `07-about` (`.png`).
