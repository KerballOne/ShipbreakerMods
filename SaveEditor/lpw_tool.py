#!/usr/bin/env python3
"""
Hardspace: Shipbreaker .lpw save-file decode/encode tool.

Reverse-engineered from decompiled BBI.Unity.Game.dll (SaveLoadManager.cs,
AssetSaveKeyMapService.cs, PlayerActionTrackerDataReaderWriterV1.cs,
PlayerGeneralDataReaderWriterV8.cs, PlayerCertificationTierDataReaderWriterV3.cs,
SceneHistoryDataReaderWriterV1.cs).

File format summary
--------------------
Header:
    b"LPW\\0"                        magic
    uint32 LE                        version (observed: 3)
    .NET 7-bit-encoded-length string  profile name (appears twice in the header;
                                       exact layout between the two occurrences
                                       was not fully mapped -- this tool locates
                                       sections by scanning for their hash rather
                                       than parsing the header positionally)

Body: a sequence of sections, each framed as:
    uint32 LE   hashedDataKey = FNV-1a32(DataKey, seed=2166136261)
    uint32 LE   version
    <payload, format depends on DataKey>
repeated until EOF. There are 22 known DataKeys (see DATA_KEYS below), always
present, always in the same order, in every save observed so far.

Per-asset references (e.g. which PlayerActionTrackerAsset a PAT entry is)
are stored as `uint32 hashedAssetKey = FNV-1a32(SaveKey, 2166136261)`, where
SaveKey is a separate per-asset string that does NOT always equal the asset's
name (confirmed empirically -- hashing names directly produced different
values than the live game-computed hash). The only reliable name<->hash table
is `asset_save_keys.json`, produced by the PartInfoLogger BepInEx mod's
`DumpCampaignProgress` (reflects `AssetSaveKeyMapService.Instance`'s live
dictionary). You MUST supply that file to resolve/encode names.

Only ActionTrackerData, RandomSceneHistory, GeneralData, and
CertificationTierData have been decoded into structured form so far. All
other sections are preserved as opaque byte blobs on read/write (safe to
round-trip, but their fields are not individually editable by this tool yet).

SAFETY: never point this tool's write functions at the live
`Saves\\Profiles` directory. Always operate on a copy. See the project's
`feedback_never_overwrite_saves` note -- this tool intentionally does not
special-case that directory, so it is your responsibility to point --out
somewhere safe.
"""

from __future__ import annotations

import argparse
import json
import struct
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional


# ---------------------------------------------------------------------------
# FNV-1a 32-bit
# ---------------------------------------------------------------------------

FNV_OFFSET_BASIS = 2166136261
FNV_PRIME = 16777619


def fnv1a32(s: str, seed: int = FNV_OFFSET_BASIS) -> int:
    h = seed
    for b in s.encode("utf-8"):
        h ^= b
        h = (h * FNV_PRIME) & 0xFFFFFFFF
    return h


# ---------------------------------------------------------------------------
# Registered save-data section keys (PlayerProfileSaveLoadManager.cs)
# ---------------------------------------------------------------------------

DATA_KEYS = [
    "AbilitiesData", "ActionTrackerData", "AvailableShipsData", "CertificationTierData",
    "CurrencyData", "DurabilityData", "GeneralData", "NarrativeData", "UpgradeData",
    "ProfileDifficultyData", "CurrentCertificationProgessData", "PastProfilesData",
    "CompletedCertificationsData", "HasPlayedBefore", "VoiceData", "OxygenDrainData",
    "MessageData", "SpaceTruckState", "RandomSceneHistory", "StickerCollectionData",
    "StickerPlacementData", "FoodChoiceData", "HabData",
]
HASH_TO_KEY = {fnv1a32(k): k for k in DATA_KEYS}
KEY_TO_HASH = {k: fnv1a32(k) for k in DATA_KEYS}

DECODED_KEYS = {"ActionTrackerData", "RandomSceneHistory", "GeneralData", "CertificationTierData"}


# ---------------------------------------------------------------------------
# Low-level readers
# ---------------------------------------------------------------------------

def read_7bit_len(data: bytes, pos: int) -> tuple[int, int]:
    """.NET BinaryWriter-style 7-bit-encoded length prefix. Returns (value, new_pos)."""
    result = 0
    shift = 0
    while True:
        b = data[pos]
        pos += 1
        result |= (b & 0x7F) << shift
        if not (b & 0x80):
            break
        shift += 7
    return result, pos


def write_7bit_len(n: int) -> bytes:
    out = bytearray()
    while True:
        b = n & 0x7F
        n >>= 7
        if n:
            out.append(b | 0x80)
        else:
            out.append(b)
            break
    return bytes(out)


# ---------------------------------------------------------------------------
# Asset name <-> hash table (from PartInfoLogger's asset_save_keys.json)
# ---------------------------------------------------------------------------

class AssetKeyMap:
    def __init__(self, hash_to_name: dict[int, str], name_to_hash: dict[str, int]):
        self.hash_to_name = hash_to_name
        self.name_to_hash = name_to_hash

    @classmethod
    def load(cls, path: Path) -> "AssetKeyMap":
        with open(path, "r", encoding="utf-8") as f:
            keymap = json.load(f)
        hash_to_name = {}
        name_to_hash = {}
        for name, info in keymap.items():
            h = info["hash"]
            hash_to_name[h] = name
            name_to_hash[name] = h
        return cls(hash_to_name, name_to_hash)

    def resolve(self, h: int) -> str:
        return self.hash_to_name.get(h, f"<unknown:{h}>")

    def hash_of(self, name: str) -> int:
        if name not in self.name_to_hash:
            raise KeyError(
                f"'{name}' not found in asset_save_keys.json. "
                "Re-run PartInfoLogger's DumpCampaignProgress in-game to refresh it "
                "(the game must have this asset loaded at the time)."
            )
        return self.name_to_hash[name]


# ---------------------------------------------------------------------------
# Parsed representation
# ---------------------------------------------------------------------------

@dataclass
class Section:
    key: str
    version: int
    offset: int          # offset of the section header (hash+version) in the original bytes
    payload: bytes        # raw payload bytes (decoded sections also keep this for round-trip safety)


@dataclass
class GeneralData:
    profile_name: str
    tutorial_completed: bool
    current_tutorial_objective_hash: int
    debt_paid_off: bool
    has_pending_shift_expenses: bool
    previous_shift_earnings: float
    reset_online_stats_required: bool
    shifts_until_polaris: int


@dataclass
class CertificationTierData:
    rank: int
    tiers: dict[int, int]
    xp: float


@dataclass
class LpwSave:
    header_prefix: bytes      # raw bytes from file start up to (not including) the first section
    sections: list[Section]
    trailer: bytes = b""      # anything after the last recognized section (should normally be empty)

    # Decoded views (populated by decode_known_sections)
    action_tracker: Optional[dict[str, int]] = None
    random_scene_history: Optional[list[str]] = None
    general_data: Optional[GeneralData] = None
    certification: Optional[CertificationTierData] = None


# ---------------------------------------------------------------------------
# Parsing
# ---------------------------------------------------------------------------

def _scan_section_offsets(data: bytes) -> list[tuple[int, str, int]]:
    """Find every (offset, key, version) by scanning for known section hashes.

    This is a brute-force scan rather than a positional header parse, because
    the exact header layout preceding the first section (profile name appears
    twice, with an 8-byte field of unknown purpose in between -- looked like a
    timestamp) was not fully reverse-engineered. Scanning for the hash marker
    is reliable because FNV-1a32 collisions among our 22 known keys are not
    observed in practice, and this matches the approach used throughout the
    investigation.
    """
    found = []
    i = 0
    n = len(data)
    while i < n - 8:
        val = struct.unpack_from("<I", data, i)[0]
        key = HASH_TO_KEY.get(val)
        if key is not None:
            ver = struct.unpack_from("<I", data, i + 4)[0]
            found.append((i, key, ver))
        i += 1
    return found


def parse(data: bytes) -> LpwSave:
    offsets = _scan_section_offsets(data)
    if not offsets:
        raise ValueError("No recognized sections found -- not a valid .lpw file?")
    # Not every one of the 23 known DataKeys is written in every file -- observed saves
    # consistently included 19 of them and omitted HasPlayedBefore, PastProfilesData,
    # CompletedCertificationsData, CurrentCertificationProgessData (likely conditional on
    # whether that data is relevant, e.g. PastProfilesData only if a profile was reset).
    # Only warn if the count is surprisingly low, which would suggest real truncation/corruption.
    if len(offsets) < 15:
        sys.stderr.write(
            f"warning: only found {len(offsets)} sections (expected ~19-23) -- "
            "file may be truncated or corrupted\n"
        )

    header_prefix = data[: offsets[0][0]]

    sections = []
    for idx, (offset, key, ver) in enumerate(offsets):
        payload_start = offset + 8
        payload_end = offsets[idx + 1][0] if idx + 1 < len(offsets) else len(data)
        payload = data[payload_start:payload_end]
        sections.append(Section(key=key, version=ver, offset=offset, payload=payload))

    save = LpwSave(header_prefix=header_prefix, sections=sections)
    return save


def decode_known_sections(save: LpwSave, keymap: AssetKeyMap) -> None:
    """Populate the decoded convenience fields on an LpwSave in place."""
    for sec in save.sections:
        if sec.key == "ActionTrackerData":
            save.action_tracker = _decode_action_tracker(sec.payload, keymap)
        elif sec.key == "RandomSceneHistory":
            save.random_scene_history = _decode_scene_history(sec.payload, keymap)
        elif sec.key == "GeneralData":
            save.general_data = _decode_general_data(sec.payload)
        elif sec.key == "CertificationTierData":
            save.certification = _decode_certification(sec.payload)


def _decode_action_tracker(payload: bytes, keymap: AssetKeyMap) -> dict[str, int]:
    p = 0
    count = struct.unpack_from("<i", payload, p)[0]
    p += 4
    result = {}
    for _ in range(count):
        h = struct.unpack_from("<I", payload, p)[0]
        p += 4
        v = struct.unpack_from("<i", payload, p)[0]
        p += 4
        result[keymap.resolve(h)] = v
    return result


def _decode_scene_history(payload: bytes, keymap: AssetKeyMap) -> list[str]:
    p = 0
    count = struct.unpack_from("<i", payload, p)[0]
    p += 4
    result = []
    for _ in range(count):
        h = struct.unpack_from("<I", payload, p)[0]
        p += 4
        result.append(keymap.resolve(h))
    return result


def _decode_general_data(payload: bytes) -> GeneralData:
    p = 0
    strlen, p = read_7bit_len(payload, p)
    name = payload[p : p + strlen].decode("utf-8")
    p += strlen
    tutorial_completed = bool(payload[p]); p += 1
    tutorial_obj_hash = struct.unpack_from("<I", payload, p)[0]; p += 4
    debt_paid_off = bool(payload[p]); p += 1
    has_pending = bool(payload[p]); p += 1
    prev_earnings = struct.unpack_from("<f", payload, p)[0]; p += 4
    reset_online = bool(payload[p]); p += 1
    shifts_until_polaris = struct.unpack_from("<i", payload, p)[0]; p += 4
    return GeneralData(
        profile_name=name,
        tutorial_completed=tutorial_completed,
        current_tutorial_objective_hash=tutorial_obj_hash,
        debt_paid_off=debt_paid_off,
        has_pending_shift_expenses=has_pending,
        previous_shift_earnings=prev_earnings,
        reset_online_stats_required=reset_online,
        shifts_until_polaris=shifts_until_polaris,
    )


def _decode_certification(payload: bytes) -> CertificationTierData:
    p = 0
    rank = struct.unpack_from("<i", payload, p)[0]; p += 4
    count = struct.unpack_from("<i", payload, p)[0]; p += 4
    tiers = {}
    for _ in range(count):
        ctype = struct.unpack_from("<i", payload, p)[0]; p += 4
        val = struct.unpack_from("<i", payload, p)[0]; p += 4
        tiers[ctype] = val
    xp = struct.unpack_from("<f", payload, p)[0]; p += 4
    return CertificationTierData(rank=rank, tiers=tiers, xp=xp)


# ---------------------------------------------------------------------------
# Encoding
# ---------------------------------------------------------------------------

def _encode_action_tracker(entries: dict[str, int], keymap: AssetKeyMap) -> bytes:
    out = bytearray()
    out += struct.pack("<i", len(entries))
    for name, value in entries.items():
        out += struct.pack("<I", keymap.hash_of(name))
        out += struct.pack("<i", value)
    return bytes(out)


def _encode_certification(cert: CertificationTierData) -> bytes:
    out = bytearray()
    out += struct.pack("<i", cert.rank)
    out += struct.pack("<i", len(cert.tiers))
    for ctype, val in cert.tiers.items():
        out += struct.pack("<i", ctype)
        out += struct.pack("<i", val)
    out += struct.pack("<f", cert.xp)
    return bytes(out)


def _encode_general_data(gd: GeneralData) -> bytes:
    out = bytearray()
    name_bytes = gd.profile_name.encode("utf-8")
    out += write_7bit_len(len(name_bytes))
    out += name_bytes
    out += struct.pack("<B", 1 if gd.tutorial_completed else 0)
    out += struct.pack("<I", gd.current_tutorial_objective_hash)
    out += struct.pack("<B", 1 if gd.debt_paid_off else 0)
    out += struct.pack("<B", 1 if gd.has_pending_shift_expenses else 0)
    out += struct.pack("<f", gd.previous_shift_earnings)
    out += struct.pack("<B", 1 if gd.reset_online_stats_required else 0)
    out += struct.pack("<i", gd.shifts_until_polaris)
    return bytes(out)


def rebuild(save: LpwSave) -> bytes:
    """Reassemble a full .lpw file from an LpwSave, using decoded fields where
    present (action_tracker / certification) and raw payload bytes otherwise.

    Call decode_known_sections() first if you intend to edit action_tracker
    or certification -- otherwise those edits won't be picked up, since this
    function only re-encodes fields that were decoded.
    """
    out = bytearray()
    out += save.header_prefix
    for sec in save.sections:
        if sec.key == "ActionTrackerData" and save.action_tracker is not None:
            payload = _encode_action_tracker(save.action_tracker, save._keymap)  # type: ignore[attr-defined]
        elif sec.key == "CertificationTierData" and save.certification is not None:
            payload = _encode_certification(save.certification)
        elif sec.key == "GeneralData" and save.general_data is not None:
            payload = _encode_general_data(save.general_data)
        else:
            payload = sec.payload
        out += struct.pack("<I", KEY_TO_HASH[sec.key])
        out += struct.pack("<I", sec.version)
        out += payload
    out += save.trailer
    return bytes(out)


# ---------------------------------------------------------------------------
# High-level convenience API
# ---------------------------------------------------------------------------

def load(path: Path, keymap: AssetKeyMap) -> LpwSave:
    data = Path(path).read_bytes()
    save = parse(data)
    decode_known_sections(save, keymap)
    save._keymap = keymap  # type: ignore[attr-defined]  -- stashed for rebuild()
    return save


def save_to(save: LpwSave, path: Path) -> None:
    data = rebuild(save)
    Path(path).write_bytes(data)


def rename_profile(save: LpwSave, new_name: str) -> None:
    """Update GeneralData.profile_name. NOTE: does not update the raw header
    prefix's duplicate profile-name string (that lives in header_prefix,
    outside any decoded section, and was not fully reverse-engineered). If
    the game reads the profile name from GeneralData rather than the header,
    this is sufficient; if it reads the header copy, you must also patch
    header_prefix manually (see the `rename-header` CLI command for a
    best-effort implementation used successfully once in the investigation
    session -- edits both occurrences of the length-prefixed name string in
    header_prefix)."""
    if save.general_data is None:
        raise ValueError("GeneralData not decoded")
    save.general_data.profile_name = new_name


def rewrite_header_profile_name(header_prefix: bytes, new_name: str) -> bytes:
    """Rewrite the profile-name string in the raw header prefix (the bytes
    before the first section marker: "LPW\\0" + uint32 version + 7-bit-len-
    prefixed name -- nothing else). This is only ONE of the two places the
    profile name is stored; the other is GeneralData.profile_name (a
    separately decoded field -- see rename_profile()). Earlier investigation
    notes describing "two occurrences in the header" were conflating this
    header copy with GeneralData's copy, which is actually the first section's
    payload, not part of the header prefix. Fixed here after finding the bug
    while building this tool.
    """
    data = header_prefix
    pos = 4 + 4  # skip "LPW\0" + uint32 version
    strlen, pos_after_len = read_7bit_len(data, pos)
    name_end = pos_after_len + strlen

    new_name_bytes = new_name.encode("utf-8")
    new_len_prefix = write_7bit_len(len(new_name_bytes))

    out = bytearray()
    out += data[:pos]
    out += new_len_prefix
    out += new_name_bytes
    out += data[name_end:]
    return bytes(out)


def set_rank(save: LpwSave, rank: int, *, trim_reached_above: bool = True,
             trim_shift_tracker_above: bool = True) -> None:
    """Set CurrentCertificationRank and optionally remove PAT_RankXX_Reached /
    PAT_CMP_RankXX_ShiftTracker entries for ranks above the new value, matching
    the rank-rollback test plan (see project_industrial_action_investigation
    memory: "Fields to edit" list). Does NOT touch currency/upgrades/
    durability/available-ships/XP/NonSeq-rank-window PATs -- add those
    manually if your test needs them.
    """
    if save.certification is None:
        raise ValueError("CertificationTierData not decoded")
    save.certification.rank = rank

    if save.action_tracker is None:
        return

    if trim_reached_above:
        for r in range(rank + 1, 100):
            save.action_tracker.pop(f"PAT_Rank{r:02d}_Reached", None)

    if trim_shift_tracker_above:
        for r in range(rank + 1, 100):
            save.action_tracker.pop(f"PAT_CMP_Rank{r:02d}_ShiftTracker", None)


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def _cmd_dump(args: argparse.Namespace) -> None:
    keymap = AssetKeyMap.load(Path(args.keymap))
    save = load(Path(args.file), keymap)

    print(f"=== {args.file} ===")
    print(f"Sections ({len(save.sections)}):")
    for sec in save.sections:
        print(f"  offset={sec.offset:6d}  {sec.key} (v{sec.version})  payload={len(sec.payload)} bytes"
              f"{'  [decoded]' if sec.key in DECODED_KEYS else ''}")
    print()

    if save.general_data:
        print("--- GeneralData ---")
        gd = save.general_data
        for k, v in vars(gd).items():
            print(f"  {k}: {v}")
        print()

    if save.certification:
        print("--- CertificationTierData ---")
        print(f"  rank: {save.certification.rank}")
        print(f"  xp: {save.certification.xp}")
        print(f"  tiers: {save.certification.tiers}")
        print()

    if save.action_tracker is not None:
        unresolved = [k for k in save.action_tracker if k.startswith("<unknown")]
        print(f"--- ActionTrackerData: {len(save.action_tracker)} entries ({len(unresolved)} unresolved) ---")
        for name in sorted(save.action_tracker):
            print(f"  {name}: {save.action_tracker[name]}")
        print()

    if save.random_scene_history is not None:
        print(f"--- RandomSceneHistory: {len(save.random_scene_history)} entries ---")
        for name in sorted(save.random_scene_history):
            print(f"  {name}")


def _cmd_set_pat(args: argparse.Namespace) -> None:
    keymap = AssetKeyMap.load(Path(args.keymap))
    save = load(Path(args.file), keymap)
    if save.action_tracker is None:
        raise SystemExit("ActionTrackerData section not found in this file")
    if args.value is None:
        save.action_tracker.pop(args.pat, None)
        print(f"Removed {args.pat}")
    else:
        save.action_tracker[args.pat] = args.value
        print(f"Set {args.pat} = {args.value}")
    save_to(save, Path(args.out))
    print(f"Wrote {args.out}")


def _cmd_set_rank(args: argparse.Namespace) -> None:
    keymap = AssetKeyMap.load(Path(args.keymap))
    save = load(Path(args.file), keymap)
    old_rank = save.certification.rank if save.certification else None
    set_rank(save, args.rank,
              trim_reached_above=not args.no_trim,
              trim_shift_tracker_above=not args.no_trim)
    print(f"Rank: {old_rank} -> {args.rank}"
          f"{' (trimmed Reached/ShiftTracker PATs above new rank)' if not args.no_trim else ''}")
    save_to(save, Path(args.out))
    print(f"Wrote {args.out}")


def _cmd_rename(args: argparse.Namespace) -> None:
    keymap = AssetKeyMap.load(Path(args.keymap))
    save = load(Path(args.file), keymap)
    rename_profile(save, args.new_name)                              # GeneralData copy
    save.header_prefix = rewrite_header_profile_name(save.header_prefix, args.new_name)  # header copy
    save_to(save, Path(args.out))
    new_hash = fnv1a32(args.new_name)
    print(f"Renamed profile to '{args.new_name}' (both header and GeneralData copies)")
    print(f"Wrote {args.out}")
    print(f"Target Saves/Profiles filename would be: vglpp3_{new_hash}.lpw")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--keymap", default="asset_save_keys.json",
                     help="Path to PartInfoLogger's asset_save_keys.json (default: %(default)s)")
    sub = ap.add_subparsers(dest="command", required=True)

    p_dump = sub.add_parser("dump", help="Decode and print a .lpw file")
    p_dump.add_argument("file")
    p_dump.set_defaults(func=_cmd_dump)

    p_setpat = sub.add_parser("set-pat", help="Set (or remove, with no --value) one PAT entry and write a new file")
    p_setpat.add_argument("file")
    p_setpat.add_argument("pat", help="PAT asset name, e.g. PAT_CMP_17_X1_LouRecruitsCrew_Complete")
    p_setpat.add_argument("--value", type=int, default=None, help="New value; omit to remove the entry")
    p_setpat.add_argument("--out", required=True, help="Output path (never point this at Saves/Profiles directly)")
    p_setpat.set_defaults(func=_cmd_set_pat)

    p_rank = sub.add_parser("set-rank", help="Set CurrentCertificationRank (and trim Reached/ShiftTracker PATs above it)")
    p_rank.add_argument("file")
    p_rank.add_argument("rank", type=int)
    p_rank.add_argument("--no-trim", action="store_true", help="Don't remove PAT_RankXX_Reached/ShiftTracker above the new rank")
    p_rank.add_argument("--out", required=True)
    p_rank.set_defaults(func=_cmd_set_rank)

    p_rename = sub.add_parser("rename-header", help="Rewrite the profile-name string(s) in the raw file header (for creating a new test profile)")
    p_rename.add_argument("file")
    p_rename.add_argument("new_name")
    p_rename.add_argument("--out", required=True)
    p_rename.set_defaults(func=_cmd_rename)

    args = ap.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
