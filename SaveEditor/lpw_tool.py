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
import re
import struct
import sys
from dataclasses import dataclass, field
from datetime import datetime, timezone
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

DECODED_KEYS = {"ActionTrackerData", "RandomSceneHistory", "GeneralData", "CertificationTierData", "MessageData"}


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
# Certification XP thresholds (from PartInfoLogger's certification_levels.json,
# added 2026-07-08 -- see PartInfoLogger/Plugin.cs DumpCertificationLevels).
# Indexed by rank-1 (rank 1 -> index 0), matching CertificationLevelAssets'
# in-game array indexing. Used by goto-milestone's XP auto-calculation.
# ---------------------------------------------------------------------------

def _load_certification_levels(keymap_path: Path) -> list[dict]:
    candidates = [
        keymap_path.parent / "certification_levels.json",
        Path("certification_levels.json"),
    ]
    for candidate in candidates:
        if candidate.exists():
            with open(candidate, "r", encoding="utf-8") as f:
                entries = json.load(f)
            return sorted(entries, key=lambda e: e["rank"])
    return []


_CERTIFICATION_LEVELS: list[dict] = []


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
    message_history: Optional[dict[str, int]] = None  # NarrativeMessageAsset name -> Unix epoch seconds


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
        elif sec.key == "MessageData":
            save.message_history = _decode_message_data(sec.payload, keymap)


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


def _decode_message_data(payload: bytes, keymap: AssetKeyMap) -> dict[str, int]:
    """MessageData: count(int32) + count * (hash:uint32, unixEpochSeconds:int64).

    Reverse-engineered 2026-07-08 during the Industrial Action investigation:
    this is a per-NarrativeMessageAsset "first delivered/read" timestamp table,
    entirely separate from ActionTrackerData's PAT completion flags. Critically,
    this section is UNTOUCHED by set_rank()/goto_milestone() -- confirmed live
    that a message (and its read state) can remain visible/interactable in a
    profile's inbox after every relevant ActionTrackerData PAT (including the
    entire main-story/Act3 chain) has been stripped, because the message's
    entry here was never removed. Re-opening such a message re-posts its
    read-PAT and can retrigger downstream PATConditionalTriggerComponent
    scenes, bypassing the intended linear story gate entirely -- this is the
    root cause of an otherwise-inexplicable retrigger seen on a from-scratch
    Beltalowda clone with the full Act 3 PAT chain removed.

    4 trailing zero bytes observed after the last entry in every save examined
    so far -- likely an empty second count-prefixed list (unknown purpose,
    possibly "sent" messages or similar) -- preserved as `trailer` but not
    itself decoded pending a save with a nonzero value there.
    """
    p = 0
    count = struct.unpack_from("<i", payload, p)[0]
    p += 4
    result = {}
    for _ in range(count):
        h = struct.unpack_from("<I", payload, p)[0]
        p += 4
        ts = struct.unpack_from("<q", payload, p)[0]
        p += 8
        result[keymap.resolve(h)] = ts
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


def _encode_message_data(entries: dict[str, int], keymap: AssetKeyMap) -> bytes:
    """Inverse of _decode_message_data. Appends the 4 trailing zero bytes
    observed after the entry list in every save examined so far (see the
    decoder's docstring) -- if a future save turns up a nonzero value there,
    this encoder will silently corrupt it; not yet handled since no such
    save has been found.
    """
    out = bytearray()
    out += struct.pack("<i", len(entries))
    for name, ts in entries.items():
        out += struct.pack("<I", keymap.hash_of(name))
        out += struct.pack("<q", ts)
    out += b"\x00\x00\x00\x00"
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
        elif sec.key == "MessageData" and save.message_history is not None:
            payload = _encode_message_data(save.message_history, save._keymap)  # type: ignore[attr-defined]
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

    save.action_tracker["PAT_STICKERS_EmployeeAdvancement_RankUp"] = rank


# ---------------------------------------------------------------------------
# Milestone "time machine" -- roll a save back to just before a specific
# numbered story milestone, so its trigger can be re-approached from below.
#
# Full ordered chain, confirmed via Beltalowda's fully-completed save
# (project_industrial_action_investigation memory). Each entry's rank is the
# leading number in its own PAT name -- confirmed as a strong but NOT
# code-verified naming convention (the real gate is a separate serialized
# m_RequiredLevel/PATConditionAsset field not visible in decompiled IL).
# ---------------------------------------------------------------------------

MILESTONE_CHAIN = [
    ("PAT_CMP_02_00_HABIntro_Night_Complete", 2),
    ("PAT_CMP_02_01_WeaverBackstory_Tracker", 2),
    ("PAT_CMP_04_01_LouHopes_Night_Complete", 4),
    ("PAT_CMP_05_01_LouUnionNewsletterSignup_Complete", 5),
    ("PAT_CMP_05_02_WeaverLouFriction_ScenePlayed", 5),
    ("PAT_CMP_07_00_CalyssiaAntiUnion_Complete", 7),
    ("PAT_CMP_09_00_RhodesArrives_Complete", 9),
    ("PAT_CMP_10_00_RhodesUpsHazardLevel_Complete", 10),
    ("PAT_CMP_11_00_RhodesDemoCharges_Complete", 11),
    ("PAT_CMP_11_01_CrewPrivateComms_Night_Complete", 11),
    ("PAT_CMP_12_00_RhodesPowerGens_Complete", 12),
    ("PAT_CMP_13_01_CrewCommiserates_Night_Complete", 13),
    ("PAT_CMP_14_00_RhodesRadiation_Complete", 14),
    ("PAT_CMP_15_00_RhodesCutter1on1_Night_Complete", 15),
    ("PAT_CMP_16_01_LouResolveWaning_Complete", 16),
    ("PAT_CMP_17_01_KaitoScrewUpWarning_Complete", 17),
    ("PAT_CMP_17_02_KaitoPulledAside_Complete", 17),
    ("PAT_CMP_17_02_WeaverUpsetAboutKaito_Complete", 17),
    ("PAT_CMP_17_03_KaitoPunished_Complete", 17),
    ("PAT_CMP_17_03_LouUpsetAboutKaito_Complete", 17),
    ("PAT_CMP_17_03_LouUpsetAboutKaito_Night_Complete", 17),
    ("PAT_CMP_17_X1_LouRecruitsCrew_Complete", 17),
    ("PAT_CMP_17_X2_LYNXUnionClampDown_Complete", 17),
]

MILESTONE_NAMES = [name for name, _ in MILESTONE_CHAIN]

# ---------------------------------------------------------------------------
# Message-to-checkpoint matrix (checkpoint_matrix.md, built 2026-07-08).
#
# Each entry is (message_asset_name, gap_start, gap_end), where gap_start is
# the milestone/scene name the message becomes relevant AT OR AFTER, and
# gap_end is the next confirmed anchor (exclusive) -- i.e. the message
# belongs somewhere in (gap_start, gap_end], but its exact position within
# that range is NOT confirmed for "?"-confidence entries. Per the directional
# rule agreed 2026-07-08: rolling BACKWARD past gap_start removes the
# message; filling FORWARD to at/past gap_start adds it. Confirmed (C) and
# inferred-by-name (I) entries have gap_start == gap_end == the one PAT they
# actually map to.
#
# gap_end of None means "no known upper bound" (extends past 17_X2 into
# Act 3, not yet further mapped).
# ---------------------------------------------------------------------------

MESSAGE_MAP = [
    ("NARCON_Message_Union_Welcome", "PAT_CMP_02_00_HABIntro_Night_Complete", "PAT_CMP_02_00_HABIntro_Night_Complete"),
    ("NARCON_Message_Lou_UnionSignup", "PAT_CMP_05_01_LouUnionNewsletterSignup_Complete", "PAT_CMP_05_01_LouUnionNewsletterSignup_Complete"),
    ("NARCON_Message_Union_Contracts", "PAT_CMP_05_02_WeaverLouFriction_ScenePlayed", "PAT_CMP_09_00_RhodesArrives_Complete"),
    ("NARCON_Message_Union_CancelDebt", "PAT_CMP_05_02_WeaverLouFriction_ScenePlayed", "PAT_CMP_09_00_RhodesArrives_Complete"),
    ("NARCON_Message_LynxInternal_IncomingTransmission", "PAT_CMP_07_00_CalyssiaAntiUnion_Complete", "PAT_CMP_09_00_RhodesArrives_Complete"),
    ("NARCON_Message_Union_LouInterview", "PAT_CMP_07_00_CalyssiaAntiUnion_Complete", "PAT_CMP_09_00_RhodesArrives_Complete"),
    ("NARCON_Message_Rhodes_Arrives", "PAT_CMP_09_00_RhodesArrives_Complete", "PAT_CMP_09_00_RhodesArrives_Complete"),
    ("NARCON_Message_Union_Admins", "PAT_CMP_16_01_LouResolveWaning_Complete", "PAT_CMP_17_X2_LYNXUnionClampDown_Complete"),
    ("NARCON_Message_Union_Crackdowns", "PAT_CMP_16_01_LouResolveWaning_Complete", "PAT_CMP_17_X2_LYNXUnionClampDown_Complete"),
    ("NARCON_Message_Union_CallToAction", "PAT_CMP_16_01_LouResolveWaning_Complete", "PAT_CMP_17_X2_LYNXUnionClampDown_Complete"),
    ("NARCON_Message_LynxInternal_UnionClampDown", "PAT_CMP_17_X2_LYNXUnionClampDown_Complete", "PAT_CMP_17_X2_LYNXUnionClampDown_Complete"),
    ("NARCON_Message_Lou_MessageFromLou", "PAT_CMP_A3_SC04_MessageFromLou_Read", None),
    ("NARCON_Message_LynxInternal_DebtComplete", "PAT_CMP_A3_SC09_OutroArbitration_Complete", None),
]

# Act 3 sub-chain, appended after MILESTONE_CHAIN for message-gap purposes only
# (goto_milestone's rank logic doesn't need these -- they're all rank 17).
# SC10 deliberately excluded: confirmed optional/conditional (absent from
# every save examined 2026-07-08 including Beltalowda's fully-completed one)
# -- do not strip-by-default or fill-by-default, only touch explicitly.
_ACT3_ORDER = [
    "PAT_CMP_A3_SC01_CrewInDisarray_Complete",
    "PAT_CMP_A3_SC02_KaitoConfesses_Complete",
    "PAT_CMP_A3_SC03_FullOnDespair_Complete",
    "PAT_CMP_A3_SC04_MessageFromLou_Read",
    "PAT_CMP_A3_SC05_CrewUnites_Complete",
    "PAT_CMP_A3_SC06_IndustrialActionIntroVO_Complete",
    "PAT_CMP_A3_SC07_SceneComplete",
    "PAT_CMP_A3_SC08_IndustrialActionOutro_Complete",
    "PAT_CMP_A3_SC09_OutroArbitration_Complete",
    "PAT_CMP_A3_SC11_TheWayOut_Complete",
    "PAT_CMP_A3_SC12_TheNewNormal_Complete",
]
_CHECKPOINT_ORDER = MILESTONE_NAMES + _ACT3_ORDER




# Matches sequential story-content PATs that should be stripped by goto_milestone
# when rolling back to/through 17_X1 or 17_X2: the numbered main-story chain
# (PAT_CMP_02_00_..., PAT_CMP_17_03_..., etc.) and the undocumented downstream
# Act 3 sub-chain (PAT_CMP_A3_SC01_... through SC12_..., found in Beltalowda's
# save on 2026-07-08 -- NOT previously tracked in MILESTONE_CHAIN, includes
# dozens of per-goal/per-scene sub-PATs like PAT_CMP_A3_SC07_RF_1-3_SalvDestroyed_01_Complete).
# Deliberately excludes PAT_CMP_NONSEQ_... (non-sequential ambient/rank-window
# content, e.g. Rank_09_12_Start/End, DeedeeChatter) and other bookkeeping
# families (Overall_ShiftTracker, RankXX_ShiftTracker) which set_rank() already
# handles separately -- these are NOT part of the linear story-progress chain
# and must not be wiped just because a later numbered milestone is targeted.
_SEQUENTIAL_STORY_PAT_RE = re.compile(r"^PAT_CMP_(\d{2}_|A3_)")


def goto_milestone(save: LpwSave, target: str, *, rank_offset: int = 0,
                    preserve_target: bool = False,
                    strip_downstream_story_pats: bool = True,
                    strip_messages: bool = True) -> int:
    """Roll a save back to just BEFORE `target` -- `target` itself (and
    everything after it in the chain) is REMOVED by default, so it
    retriggers on next load. This is the final semantics as of 2026-07-08
    (two earlier revisions this same day flip-flopped on this: first
    "strip target," then briefly "preserve target" per user feedback, now
    reverted back to "strip target" as the more natural reading of a GOTO
    command -- the user expects PAT_CMP_X to actually fire when they say
    "goto PAT_CMP_X").

    Pass preserve_target=True to instead land ON `target` with it kept
    (useful for setting up a save at a specific completed checkpoint without
    wanting anything to retrigger). Rank is NEVER simply target's rank
    number directly -- it's always computed as the highest rank among
    whatever milestones remain preserved after stripping (i.e. the max rank
    in MILESTONE_CHAIN[:cutoff]). This matters because
    multiple milestones can share the same rank (five sit at rank 17 before
    17_X1, for example) -- stripping just the last of a same-rank cluster
    must NOT drop rank below what the still-preserved same-rank milestones
    require (2026-07-08: caught a bug where stripping
    17_03_LouUpsetAboutKaito_Night_Complete would have incorrectly computed
    rank 16, even though 17_01/17_02 (x2)/17_03_KaitoPunished/
    17_03_LouUpsetAboutKaito -- all rank 17 -- remain preserved before it).

      1. CurrentCertificationRank set to target's OWN rank (plus
         rank_offset, e.g. +1 to sit a rank higher if a wider approach
         window from below the NEXT milestone is wanted), via set_rank()
         -- trims Reached/ShiftTracker above and fixes
         EmployeeAdvancement_RankUp.
      2. Every milestone STRICTLY AFTER `target` in the chain is REMOVED
         entirely (not set to 0) from ActionTrackerData if present --
         confirmed this session that real saves never carry a not-yet-
         triggered milestone PAT at value 0, only fully absent; leaving a
         stale `key: 0` entry was the likely reason two earlier retrigger
         attempts failed even with the correct rank already rolled back.
         `target` itself is left in place -- it's the checkpoint being
         landed on, not the one being retriggered.
      3. Every milestone AT OR BEFORE `target` in the chain is left
         untouched -- does not force-add earlier milestones the save
         doesn't have, since doing so was tried once already (heidi_test1's
         original fill-in attempt) and made no difference to retriggering.
      4. If `target` is 17_X1, 17_X2, or later (i.e. strip_downstream_story_pats
         is True and the target's index is at/after 17_X1's), ALSO removes
         every PAT_CMP_NN_... and PAT_CMP_A3_... entry STRICTLY AFTER
         `target` in the save, not just the ones listed in MILESTONE_CHAIN
         -- this catches the large, previously untracked PAT_CMP_A3_SC01
         through SC12 downstream sub-chain (found 2026-07-08 still present
         in a Beltalowda clone after targeting 17_X1 -- MILESTONE_CHAIN
         alone missed it entirely). Does NOT touch PAT_CMP_NONSEQ_... or
         other non-sequential/bookkeeping PATs -- pass
         strip_downstream_story_pats=False to disable this step entirely.
      5. If strip_messages is True, ALSO removes every MessageData entry
         (see checkpoint_matrix.md / MESSAGE_MAP) STRICTLY AFTER `target`
         in story order -- this closes the bypass discovered live: a
         message's delivered/read record in MessageData survives steps 1-4
         untouched, and reopening an already-read message can re-post its
         read-PAT and retrigger a downstream scene EVEN THOUGH the upstream
         milestone chain was just stripped, because message delivery is
         tracked completely independently of ActionTrackerData. Messages
         with only a "gap"-level mapping (not pinned to one exact PAT) are
         stripped as a whole group per the directional rule: if `target`
         falls anywhere strictly before a message's gap upper bound, that
         message is removed too, even without an exact PAT match. Requires
         save.message_history to be populated (i.e. keymap available);
         silently skipped if not.

    Does NOT touch AvailableShipsData, currency, upgrades, durability, XP,
    or NonSeq rank-window PATs (Rank_05_08/09_12/13_16 Start/End) -- add
    those manually if a specific test needs them, same scope discipline as
    set_rank(). Does NOT touch PAT_CMP_A3_SC10_FeeReduction_Complete (see
    checkpoint_matrix.md -- confirmed optional/conditional, skip-by-default).

    Returns the rank the save was set to, for confirmation/logging.
    """
    names = [name for name, _ in MILESTONE_CHAIN]
    if target not in names:
        raise KeyError(f"'{target}' not in MILESTONE_CHAIN. Known milestones: {', '.join(names)}")

    idx = names.index(target)
    # cutoff = index one past the last PRESERVED milestone. If preserving
    # target, that's target's own index (inclusive); if not, it's idx-1.
    cutoff = idx if preserve_target else idx - 1
    preserved = MILESTONE_CHAIN[: cutoff + 1]
    base_rank = max((r for _, r in preserved), default=1)
    new_rank = base_rank + rank_offset

    set_rank(save, new_rank)

    if save.action_tracker is not None:
        for name, _ in MILESTONE_CHAIN[cutoff + 1:]:
            save.action_tracker.pop(name, None)

        x1_idx = names.index("PAT_CMP_17_X1_LouRecruitsCrew_Complete")
        if strip_downstream_story_pats and cutoff + 1 >= x1_idx:
            kept_names = {n for n, _ in preserved}  # everything preserved must survive the sweep
            for name in list(save.action_tracker):
                if name in kept_names:
                    continue
                if _SEQUENTIAL_STORY_PAT_RE.match(name):
                    save.action_tracker.pop(name, None)

    if strip_messages and save.message_history is not None:
        cutoff_checkpoint_idx = _CHECKPOINT_ORDER.index(names[cutoff]) if cutoff >= 0 else -1
        for msg_name, gap_start, _gap_end in MESSAGE_MAP:
            if gap_start not in _CHECKPOINT_ORDER:
                continue
            if _CHECKPOINT_ORDER.index(gap_start) > cutoff_checkpoint_idx:
                save.message_history.pop(msg_name, None)

    return new_rank


def fill_to_milestone(save: LpwSave, reference: LpwSave, target: str, *,
                       fill_messages: bool = True) -> None:
    """Inverse of goto_milestone: patch `save` FORWARD to have completed
    everything up to and including `target`, copying actual values from
    `reference` (intended to be a known-good, fully-completed save such as
    Beltalowda's) rather than fabricating placeholder values.

    For each milestone in MILESTONE_CHAIN at or before `target` (main-story
    chain only -- does NOT walk the Act 3 sub-chain, since fill-forward
    across A3_SC content hasn't been needed/tested yet): if `save` is
    missing the PAT, copies `reference`'s value for it verbatim (not
    hardcoded to 1, in case a PAT's real completed value differs, e.g.
    PAT_CMP_11_00_RhodesDemoCharges_Complete was seen at 2 in Beltalowda's
    save, not 1).

    If fill_messages is True (default), also copies MessageData entries
    (hash + REFERENCE's own delivery timestamp, per the directional gap
    rule -- see checkpoint_matrix.md) for every message whose gap_start is
    at/before `target`, if `save` doesn't already have that message.

    Does NOT touch rank/XP -- call set_rank() separately if the save's rank
    also needs to move forward to match. Does NOT touch SC10 (optional,
    skip-by-default -- see checkpoint_matrix.md) or anything not present in
    `reference` itself (can't fill from a hole that's also in the reference).
    """
    if save.action_tracker is None or reference.action_tracker is None:
        raise ValueError("ActionTrackerData not decoded on save and/or reference")

    names = [name for name, _ in MILESTONE_CHAIN]
    if target not in names:
        raise KeyError(f"'{target}' not in MILESTONE_CHAIN. Known milestones: {', '.join(names)}")
    idx = names.index(target)

    for name in names[: idx + 1]:
        if name in save.action_tracker:
            continue
        if name in reference.action_tracker:
            save.action_tracker[name] = reference.action_tracker[name]

    if fill_messages and save.message_history is not None and reference.message_history is not None:
        wanted = {name for name, gap_start, _ in MESSAGE_MAP
                  if gap_start in _CHECKPOINT_ORDER and _CHECKPOINT_ORDER.index(gap_start) <= idx}
        for msg_name in wanted:
            if msg_name in save.message_history:
                continue
            if msg_name in reference.message_history:
                save.message_history[msg_name] = reference.message_history[msg_name]


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
        print()

    if save.message_history is not None:
        unresolved = [k for k in save.message_history if k.startswith("<unknown")]
        print(f"--- MessageData: {len(save.message_history)} entries ({len(unresolved)} unresolved) ---")
        for name in sorted(save.message_history, key=lambda k: save.message_history[k]):
            ts = save.message_history[name]
            iso = datetime.fromtimestamp(ts, tz=timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")
            print(f"  {name}: {ts} ({iso})")


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


def _snapshot(save: LpwSave) -> dict:
    """Capture a comparable snapshot of every decoded field, for diffing
    before/after a goto-milestone or fill-milestone run."""
    return {
        "rank": save.certification.rank if save.certification else None,
        "xp": save.certification.xp if save.certification else None,
        "action_tracker": dict(save.action_tracker) if save.action_tracker is not None else {},
        "message_history": dict(save.message_history) if save.message_history is not None else {},
    }


def _print_diff(before: dict, after: dict) -> None:
    """Print a full before/after diff: rank, xp, and every ActionTrackerData/
    MessageData key that was added, removed, or changed value."""
    print()
    print("=== DIFF ===")
    if before["rank"] != after["rank"]:
        print(f"  rank:  {before['rank']} -> {after['rank']}")
    if before["xp"] != after["xp"]:
        print(f"  xp:    {before['xp']} -> {after['xp']}")

    at_before, at_after = before["action_tracker"], after["action_tracker"]
    removed = sorted(set(at_before) - set(at_after))
    added = sorted(set(at_after) - set(at_before))
    changed = sorted(k for k in set(at_before) & set(at_after) if at_before[k] != at_after[k])

    if removed:
        print(f"\n  ActionTrackerData removed ({len(removed)}):")
        for k in removed:
            print(f"    - {k}: {at_before[k]}")
    if added:
        print(f"\n  ActionTrackerData added ({len(added)}):")
        for k in added:
            print(f"    + {k}: {at_after[k]}")
    if changed:
        print(f"\n  ActionTrackerData changed ({len(changed)}):")
        for k in changed:
            print(f"    ~ {k}: {at_before[k]} -> {at_after[k]}")

    msg_before, msg_after = before["message_history"], after["message_history"]
    msg_removed = sorted(set(msg_before) - set(msg_after))
    msg_added = sorted(set(msg_after) - set(msg_before))

    if msg_removed:
        print(f"\n  MessageData removed ({len(msg_removed)}):")
        for k in msg_removed:
            print(f"    - {k}")
    if msg_added:
        print(f"\n  MessageData added ({len(msg_added)}):")
        for k in msg_added:
            print(f"    + {k}")

    if not (removed or added or changed or msg_removed or msg_added
            or before["rank"] != after["rank"] or before["xp"] != after["xp"]):
        print("  (no changes)")
    print()


def _cmd_goto_milestone(args: argparse.Namespace) -> None:
    keymap = AssetKeyMap.load(Path(args.keymap))
    save = load(Path(args.file), keymap)
    before = _snapshot(save)

    new_rank = goto_milestone(save, args.milestone, rank_offset=args.rank_offset,
                               preserve_target=args.preserve_target,
                               strip_messages=not args.no_messages)

    print(f"Rank: {before['rank']} -> {new_rank}")
    print(f"Removed '{args.milestone}' and every later milestone in the chain (if present)")

    if not args.no_xp and save.certification is not None:
        # The HUD progress bar shows (CurrentXP - rank[N-1].RequiredXP) out of
        # (rank[N].RequiredXP - rank[N-1].RequiredXP) when at rank N -- i.e.
        # it's progress toward completing the save's OWN current rank, not
        # toward the next one. So "5 XP of headroom" means CurrentXP should
        # sit 5 below THIS rank's own threshold (new_rank's), not next
        # rank's -- confirmed the hard way 2026-07-08: an earlier version of
        # this code used new_rank+1's threshold, which produced xp=5340 at
        # rank 17 and rendered as an overflowing 1440/625 bar instead of the
        # intended 620/625.
        levels_by_rank = {e["rank"]: e["requiredXP"] for e in _CERTIFICATION_LEVELS}
        if new_rank in levels_by_rank:
            required_xp = levels_by_rank[new_rank]
            new_xp = required_xp - 5.0
            save.certification.xp = new_xp
            print(f"xp: {before['xp']} -> {new_xp} (5 below rank {new_rank}'s own {required_xp} threshold)")
        else:
            print(f"xp: left at {before['xp']} (certification_levels.json not found/missing rank {new_rank} -- "
                  f"run PIL in-game to refresh it, or pass --no-xp to suppress this warning)")

    if not args.no_diff:
        _print_diff(before, _snapshot(save))

    save_to(save, Path(args.out))
    print(f"Wrote {args.out}")


def _cmd_fill_milestone(args: argparse.Namespace) -> None:
    keymap = AssetKeyMap.load(Path(args.keymap))
    save = load(Path(args.file), keymap)
    reference = load(Path(args.reference), keymap)
    before = _snapshot(save)

    fill_to_milestone(save, reference, args.milestone, fill_messages=not args.no_messages)

    pats_added = set(save.action_tracker or {}) - set(before["action_tracker"])
    messages_added = set(save.message_history or {}) - set(before["message_history"])

    print(f"Filled forward to '{args.milestone}' using {args.reference} as reference")
    if pats_added:
        print(f"Added {len(pats_added)} PAT(s): {', '.join(sorted(pats_added))}")
    else:
        print("No PATs needed adding (save already had everything up to the target)")
    if messages_added:
        print(f"Added {len(messages_added)} MessageData entr{'y' if len(messages_added)==1 else 'ies'}: "
              f"{', '.join(sorted(messages_added))}")

    if not args.no_diff:
        _print_diff(before, _snapshot(save))

    save_to(save, Path(args.out))
    print(f"Wrote {args.out}")


def _cmd_list_milestones(args: argparse.Namespace) -> None:
    for name, rank in MILESTONE_CHAIN:
        print(f"  rank {rank:2d}  {name}")


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

    p_goto = sub.add_parser("goto-milestone",
                             help="Roll a save back to just before a numbered story milestone, replicating "
                                  "the confirmed 17_X1 retrigger recipe (rank rollback + true removal, not "
                                  "zeroing, of the target and all later milestones)")
    p_goto.add_argument("file")
    p_goto.add_argument("milestone", help="Target PAT name, e.g. PAT_CMP_17_X2_LYNXUnionClampDown_Complete "
                                           "(see 'list-milestones' for the full chain)")
    p_goto.add_argument("--rank-offset", type=int, default=0,
                         help="Extra rank adjustment on top of the computed rank (default: 0)")
    p_goto.add_argument("--preserve-target", action="store_true",
                         help="Land ON the target checkpoint with it kept, instead of stripping it too "
                              "(default: target is stripped, so it retriggers on next load)")
    p_goto.add_argument("--no-messages", action="store_true",
                         help="Don't strip MessageData entries at/after the target (see checkpoint_matrix.md "
                              "-- disabling this risks the already-read-message bypass found 2026-07-08)")
    p_goto.add_argument("--no-xp", action="store_true",
                         help="Don't auto-set xp to 5 below the new rank's next threshold "
                              "(requires certification_levels.json next to --keymap; see PartInfoLogger)")
    p_goto.add_argument("--no-diff", action="store_true",
                         help="Don't print the full before/after diff (rank, xp, every ActionTrackerData/"
                              "MessageData key added/removed/changed)")
    p_goto.add_argument("--out", required=True)
    p_goto.set_defaults(func=_cmd_goto_milestone)

    p_fill = sub.add_parser("fill-milestone",
                             help="Inverse of goto-milestone: patch a save FORWARD to have completed "
                                  "everything up to and including a milestone, copying real PAT/message "
                                  "values from a known-good reference save (e.g. Beltalowda's)")
    p_fill.add_argument("file")
    p_fill.add_argument("milestone", help="Target PAT name to fill forward to (inclusive)")
    p_fill.add_argument("--reference", required=True, help="Path to a fully-completed reference .lpw")
    p_fill.add_argument("--no-messages", action="store_true",
                         help="Don't copy MessageData entries at/before the target from the reference")
    p_fill.add_argument("--no-diff", action="store_true",
                         help="Don't print the full before/after diff (rank, xp, every ActionTrackerData/"
                              "MessageData key added/removed/changed)")
    p_fill.add_argument("--out", required=True)
    p_fill.set_defaults(func=_cmd_fill_milestone)

    p_list = sub.add_parser("list-milestones", help="Print the full known milestone chain (name -> rank)")
    p_list.set_defaults(func=_cmd_list_milestones)

    p_rename = sub.add_parser("rename-header", help="Rewrite the profile-name string(s) in the raw file header (for creating a new test profile)")
    p_rename.add_argument("file")
    p_rename.add_argument("new_name")
    p_rename.add_argument("--out", required=True)
    p_rename.set_defaults(func=_cmd_rename)

    args = ap.parse_args()

    global _CERTIFICATION_LEVELS
    _CERTIFICATION_LEVELS = _load_certification_levels(Path(args.keymap))

    args.func(args)


if __name__ == "__main__":
    main()
