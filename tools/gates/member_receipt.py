"""The per-member gate receipt store (#1910): one file per gate member that passed on one tree.

WHY THIS EXISTS: spec/baton.md C-12's ruling C states the measurement and the ruling, and is not
restated here. This file is only the STORE those per-member receipts live in. **The covering rule --
which members must be present for a push to skip -- is stated once, in `gates.py`, not here**; this
file writes, reads and authenticates individual receipts and decides nothing about a push.

**One writer.** `write()` below is the only thing that creates one of these files, and `gates.py` is
the only caller (through its `--record-member` front door, which is what a lane's component commands
invoke, and through its own post-run recording of every member it ran). Nothing writes a receipt for
a command it did not itself watch exit 0.

**Authentication, and its honest scope.** Each file carries an HMAC-SHA256 over its whole payload --
INCLUDING the member name -- keyed by a random per-git-dir secret this module creates on first use.
That is not a security boundary and is not claimed as one: anyone who can write into `<git-dir>` can
read the key, and the whole-run receipt `gates.py` writes is deliberately left unsigned (its own
selftest forges it in place to prove the tree/dirty/age checks discriminate). What the MAC buys is
the two forgeries that are otherwise indistinguishable from a real run:

  1. A hand-made file with the right shape and the right tree hash -- the cheapest way to make a push
     skip a gate that never ran.
  2. A GENUINE receipt copied onto another member's name. `audit-completeness` passing is not `lint`
     passing, and without the member name under the MAC the two files are byte-identical bar their
     filename.

A missing or unreadable key file makes every member receipt invalid -- fail closed, so a lost key
costs one `gates --fast` run rather than granting a skip.

**The store is append-only, and stale entries are INERT rather than leaked.** A receipt is overwritten
in place when its member runs again, so the directory holds at most one file per member name, whatever
identity it was last written at; a receipt for a tree that no longer exists simply never matches and
is never counted. There is deliberately no sweeper: nothing to reclaim (a bounded, tiny directory),
and a sweeper would have to race the overlap phase, where a dozen members record concurrently.
"""
import hashlib
import hmac
import json
import os
import time

MEMBER_DIR_NAME = "baton-gate-members"
KEY_NAME = "baton-gate-receipt.key"
KEY_BYTES = 32


def member_dir(git_dir):
    return os.path.join(git_dir, MEMBER_DIR_NAME)


def key_path(git_dir):
    return os.path.join(git_dir, KEY_NAME)


def load_key(git_dir, create=False):
    """The per-git-dir MAC key, or None. `create=True` mints one on first use (writers only).

    Exclusive create, then re-read on collision: two members recording concurrently (the overlap
    phase does exactly that) must end up with the SAME key, or each would invalidate the other's
    receipts.
    """
    path = key_path(git_dir)
    try:
        with open(path, "rb") as f:
            key = f.read().strip()
        if key:
            return key
    except OSError:
        pass
    if not create:
        return None
    try:
        fd = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
    except FileExistsError:
        return load_key(git_dir, create=False)
    except OSError:
        return None
    try:
        with os.fdopen(fd, "wb") as f:
            f.write(os.urandom(KEY_BYTES).hex().encode("ascii"))
    except OSError:
        return None
    return load_key(git_dir, create=False)


def _mac(key, payload):
    """HMAC-SHA256 over the payload's canonical JSON -- sorted keys, no whitespace, so the bytes
    signed are the bytes any reader reconstructs regardless of dict ordering."""
    canonical = json.dumps(payload, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hmac.new(key, canonical, hashlib.sha256).hexdigest()


def _receipt_path(git_dir, member):
    return os.path.join(member_dir(git_dir), f"{member}.json")


def write(git_dir, member, identity):
    """Record that `member` passed on `identity`. Best-effort: never raises.

    Same rule as `gates.py`'s own `write_receipt` -- a receipt that failed to write means the next
    push re-runs gates, which is always safe, whereas letting the failure propagate would turn an
    already-passed member into a nonzero exit.
    """
    try:
        os.makedirs(member_dir(git_dir), exist_ok=True)
        key = load_key(git_dir, create=True)
        if key is None:
            return
        payload = {
            "member": member,
            "tree": identity["tree"],
            "dirty": identity["dirty"],
            "diff_hash": identity["diff_hash"],
            "written_epoch": int(time.time()),
        }
        record = dict(payload)
        record["mac"] = _mac(key, payload)
        with open(_receipt_path(git_dir, member), "w", encoding="utf-8") as f:
            json.dump(record, f)
    except (OSError, KeyError, TypeError) as e:
        print(f"gates: could not record the {member} receipt ({e}) -- next push will just re-run gates",
              flush=True)


def delete(git_dir, member):
    """A receipt for a member that just FAILED is worse than none -- it would say pass."""
    try:
        os.remove(_receipt_path(git_dir, member))
    except OSError:
        pass


def _valid(key, member, record, identity, max_age_s, now):
    """Whether one loaded record authenticates as `member` passing on `identity`, recently enough."""
    if not isinstance(record, dict):
        return False
    mac = record.get("mac")
    payload = {k: v for k, v in record.items() if k != "mac"}
    if not isinstance(mac, str) or not hmac.compare_digest(mac, _mac(key, payload)):
        return False
    # The member name is under the MAC, but it is also checked against the name being ASKED for:
    # that is what stops a genuine receipt for one member from covering another (see the module
    # docstring's forgery 2), independent of what the file happens to be called.
    if payload.get("member") != member:
        return False
    if payload.get("tree") != identity["tree"]:
        return False
    if payload.get("dirty") != identity["dirty"] or payload.get("diff_hash") != identity["diff_hash"]:
        return False
    written = payload.get("written_epoch")
    if not isinstance(written, int) or isinstance(written, bool):
        return False
    age = now - written
    return 0 <= age <= max_age_s


def covered(git_dir, identity, members, max_age_s, now=None):
    """The subset of `members` holding a valid receipt for `identity`. Never raises.

    Every failure -- no key, no directory, unparseable JSON, a bad MAC, a different tree, a stale
    timestamp -- reads as "not covered", so a receipt can only ever narrow what a push skips.
    """
    now = int(time.time()) if now is None else now
    key = load_key(git_dir)
    if key is None:
        return set()
    found = set()
    for member in members:
        try:
            with open(_receipt_path(git_dir, member), "r", encoding="utf-8") as f:
                record = json.load(f)
        except (OSError, ValueError):
            continue
        if _valid(key, member, record, identity, max_age_s, now):
            found.add(member)
    return found
