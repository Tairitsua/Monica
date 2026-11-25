#!/usr/bin/env python3
"""Check the file paths and commit ancestry of a public publication."""

import argparse
import subprocess
import sys

PRIVATE_DOCUMENT_SUFFIXES = (
    b".doc", b".docm", b".docx",
    b".xls", b".xlsm", b".xlsx",
    b".ppt", b".pptm", b".pptx", b".pdf",
)


class PublicationRejected(Exception):
    """Report a policy violation without exposing filenames in public logs."""


def git(*args: str, stdin: bytes | None = None) -> bytes:
    result = subprocess.run(
        ["git", "--no-replace-objects", *args],
        input=stdin,
        stdout=subprocess.PIPE,
        check=True,
    )
    return result.stdout


def commit_id(revision: str) -> str:
    return git("rev-parse", "--verify", "--end-of-options", f"{revision}^{{commit}}").decode("ascii").strip()


def check_paths(paths: bytes) -> None:
    if any(path.lower().endswith(PRIVATE_DOCUMENT_SUFFIXES) for path in paths.split(b"\0")):
        raise PublicationRejected("Office documents and PDFs belong in private storage.")


def check_publication(base: str, tip: str) -> None:
    base = commit_id(base)
    tip = commit_id(tip)
    revisions = git("rev-list", "--parents", tip, f"^{base}").splitlines()
    if any(b" " not in revision for revision in revisions):
        raise PublicationRejected("Outgoing commits introduce unrelated Git history.")

    check_paths(git("ls-tree", "-r", "-z", "--name-only", tip))
    if revisions:
        commits = b"".join(revision.split(b" ", 1)[0] + b"\n" for revision in revisions)
        check_paths(git(
            "diff-tree", "--stdin", "-r", "-m", "-z", "--no-commit-id",
            "--name-only", "--no-renames", "--diff-filter=AMT", stdin=commits,
        ))


def check_push(remote: str) -> None:
    for update in sys.stdin.buffer:
        _, tip, _, before = update.split()
        if tip.strip(b"0"):
            base = before.decode("ascii") if before.strip(b"0") else f"refs/remotes/{remote}/dev"
            check_publication(base, tip.decode("ascii"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--pre-push", metavar="REMOTE", help="Read ref updates from the Git pre-push hook.")
    mode.add_argument("--base", help="Use the published revision as the baseline for an explicit check.")
    parser.add_argument("--tip", default="HEAD", help="Check this outgoing revision in explicit mode.")
    args = parser.parse_args()

    try:
        if git("rev-parse", "--is-shallow-repository").strip() == b"true":
            raise PublicationRejected("Public publication requires a Git clone with full history.")
        if args.pre_push is not None:
            check_push(args.pre_push)
        else:
            base = args.base if args.base.strip("0") else "refs/remotes/origin/dev"
            check_publication(base, args.tip)
    except (PublicationRejected, ValueError) as error:
        print(f"Publication check failed. {error}", file=sys.stderr)
        return 1
    except subprocess.CalledProcessError:
        print("Publication check failed. Fetch the published ref and verify the outgoing revision.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
