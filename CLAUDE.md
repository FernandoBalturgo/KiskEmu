# Rules for Claude

## Git remotes and branches

- `upstream` is the original project (Doxylamin/KiskEmu). `origin` is the user's fork (FernandoBalturgo/KiskEmu).
- **Never push to `upstream`, open a PR, issue or comment on it, or do anything else that writes to the original repo
  without the user's clear approval in chat for that specific action.** Approval for one push does not cover the next.
- Pushing to `origin` (the fork) is allowed only for contribution branches the user asked for.
- `main` is a clean mirror of `upstream/main`. Never commit private work to it.
- `mine` is the user's private branch (custom changes to make playing and testing easier). It is never pushed anywhere.
  This file lives only on `mine`.
- Contribution branches (fixes, features meant for a PR) always start from `upstream/main`, never from `mine`, so no
  private change leaks into a PR.

## Never guess

Never guess or make up values, behaviour, formats or causes without the user's clear permission first. Take them from
the repository, extracted game data, captures or a web search. If none of those has the answer, stop and tell the user
instead of filling in placeholders or assuming.

## Project focus

The goal is to re-create a working emulator of the game first. Custom changes come after that, and only on `mine`
unless the user says a change is meant for upstream.
