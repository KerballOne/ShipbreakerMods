"""One-time generator: dump lpw_tool.py's static Python literals (MILESTONE_CHAIN,
MESSAGE_MAP, _ACT3_ORDER) to JSON so the Node/TS port doesn't hand-retranscribe them
(avoids transcription errors on ~47 list entries). Re-run this and commit the output
whenever lpw_tool.py's source lists change.

Usage: python dump_static_data.py
Writes: ../data/milestones.json
"""
import importlib.util
import json
import sys
from pathlib import Path

LPW_TOOL_PATH = Path(__file__).resolve().parent.parent.parent / "SaveEditor" / "lpw_tool.py"
OUT_PATH = Path(__file__).resolve().parent.parent / "data" / "milestones.json"

spec = importlib.util.spec_from_file_location("lpw_tool", LPW_TOOL_PATH)
lpw_tool = importlib.util.module_from_spec(spec)
sys.modules["lpw_tool"] = lpw_tool  # dataclass introspection needs this registered before exec
spec.loader.exec_module(lpw_tool)

milestone_chain = [{"name": name, "rank": rank} for name, rank in lpw_tool.MILESTONE_CHAIN]
message_map = [
    {"messageName": name, "gapStart": gap_start, "gapEnd": gap_end}
    for name, gap_start, gap_end in lpw_tool.MESSAGE_MAP
]
act3_order = list(lpw_tool._ACT3_ORDER)
sequential_story_pat_regex = lpw_tool._SEQUENTIAL_STORY_PAT_RE.pattern

out = {
    "milestoneChain": milestone_chain,
    "messageMap": message_map,
    "act3Order": act3_order,
    "sequentialStoryPatRegex": sequential_story_pat_regex,
}

OUT_PATH.write_text(json.dumps(out, indent=2) + "\n", encoding="utf-8")
print(f"Wrote {len(milestone_chain)} milestones, {len(message_map)} message-map entries, "
      f"{len(act3_order)} Act-3 entries to {OUT_PATH}")
