# docs

| Path | Purpose |
|---|---|
| `banner.png`, `logo.png` | Brand images used by the README |
| `screenshots/` | Real screenshots of the app (one per tab) |
| `NOTES.ar.txt` | Detailed Arabic developer/user notes (formerly `README.txt`) |

## Screenshots

Screenshots are captured from the real running app by `scripts/capture-screenshots.ps1`, which the
GitHub Actions workflow runs on every build.

To put them into the repository so the README displays them:

1. GitHub → **Actions** → **Build VEXYON** → **Run workflow**
2. Tick **Commit the captured screenshots** → **Run workflow**

Or download the `screenshots` artifact of any run and copy the PNGs into `docs/screenshots/`.
Expected names: `01-start-here`, `02-chat`, `03-library`, `04-tools`, `05-settings`, `06-log`, `07-about` (`.png`).
