---
description: Review staged changes and create a commit
---

Review the currently staged changes, draft a commit message that follows the repo's style, and create the commit.

Steps:

1. Run `git status`, `git diff --staged --stat`, and `git log -10 --oneline` in parallel to see what's staged and match the repo's commit-message style.
2. Read enough of the staged diff to understand the *why* behind the change — group files by theme, not just list them. Use `git diff --staged <path>` selectively; don't dump the whole diff if it's large.
3. Draft a commit message:
   - Subject line under ~70 chars, imperative mood, focused on the user-visible change or motivation rather than file-by-file enumeration.
   - Body (when warranted) explains the *why* and any non-obvious tradeoffs. Skip the body for trivial changes.
   - Match the existing log's tone (terse, present tense, no emoji, no PR-style prefixes unless the log uses them).
4. Commit with the standard `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>` trailer using a HEREDOC.
5. Run `git status` afterward to confirm the commit landed.

Do not stage additional files, do not push, and do not amend prior commits — only commit what's already staged. If nothing is staged, report that and stop.
