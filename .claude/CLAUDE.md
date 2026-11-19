# Deficit Snatcher: rules for Claude

## Always merge to main

The owner has standing permission for this; don't wait to be asked.

- When a feature, fix or change is finished, merge it to `main` straight away. Don't wait for the owner to say
  "merge to main", and don't open a pull request unless asked.
- Before pushing: fetch and rebase onto the latest `origin/main`, and build what the change touches:
  - API: `dotnet build DeficitSnatcher.Api`
  - Site: `npx tsc --noEmit` and `npx eslint` in `DeficitSnatcher.Web`
- Push with `git push origin HEAD:main`, as a fast-forward. Never force-push `main`.
- Then update the session's working branch to match.
- If a build fails, or the rebase hits conflicts that aren't clearly yours to resolve, don't merge: say what's wrong.
- Work that's half-done stays on the working branch until it's finished.
- Merging to `main` doesn't deploy: deploys run from the site's Deploy tab (or Actions → Deploy).
