// ShipBreaker_SaveEditor frontend -- plain ES modules, no build step.
// Screens: profile picker -> save detail -> action (goto-milestone / difficulty
// mode) -> confirm -> result. State is just a few module-level variables since
// this app has no history/back-button needs beyond a single "back to profiles"
// link on every screen.

const app = document.getElementById("app");

let currentProfileId = null;

async function api(path, options) {
  const res = await fetch(`/api${path}`, {
    ...options,
    headers: { "Content-Type": "application/json", ...(options?.headers ?? {}) },
  });
  const body = await res.json();
  if (!res.ok) {
    throw new Error(body.error ?? `Request failed: ${res.status}`);
  }
  return body;
}

function el(tag, attrs = {}, children = []) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (k === "onClick") node.addEventListener("click", v);
    else if (k === "className") node.className = v;
    else if (k === "html") node.innerHTML = v;
    else node.setAttribute(k, v);
  }
  for (const child of Array.isArray(children) ? children : [children]) {
    if (child == null) continue;
    node.append(typeof child === "string" ? document.createTextNode(child) : child);
  }
  return node;
}

function renderError(err) {
  return el("div", { className: "error-box" }, `Error: ${err.message}`);
}

function backLink(label, onClick) {
  return el("button", { className: "back-link", onClick }, `< ${label}`);
}

// ---------------------------------------------------------------------------
// Screen 1: profile picker
// ---------------------------------------------------------------------------

// LYNX skull-badge icon, drawn as inline SVG (no external image assets, keeps
// the app self-contained/bundleable).
const LYNX_SKULL_SVG = `
<svg viewBox="0 0 64 64" fill="none" xmlns="http://www.w3.org/2000/svg">
  <circle cx="32" cy="26" r="20" fill="#0a0a0a"/>
  <circle cx="24" cy="24" r="5" fill="#f0c93a"/>
  <circle cx="40" cy="24" r="5" fill="#f0c93a"/>
  <path d="M22 38 L26 46 L32 40 L38 46 L42 38" stroke="#0a0a0a" stroke-width="4" fill="none" stroke-linecap="round" stroke-linejoin="round"/>
  <rect x="14" y="20" width="6" height="10" rx="3" fill="#0a0a0a"/>
  <rect x="44" y="20" width="6" height="10" rx="3" fill="#0a0a0a"/>
  <circle cx="47" cy="44" r="6" fill="none" stroke="#0a0a0a" stroke-width="3"/>
</svg>`;

// Deterministic-looking barcode pattern generated from the profile's file
// name, purely decorative -- CSS repeating-linear-gradient, no image asset.
// Fixed segment count (not width-driven termination) so the bar count is
// always the same regardless of hash luck -- 48 segments, comfortably above
// the "more than 32" requirement.
const BARCODE_SEGMENTS = 48;
function barcodeGradient(seed) {
  let hash = 0;
  for (const ch of seed) hash = (hash * 31 + ch.charCodeAt(0)) >>> 0;
  const stops = [];
  const segmentWidth = 100 / BARCODE_SEGMENTS;
  for (let i = 0; i < BARCODE_SEGMENTS; i++) {
    hash = (hash * 1103515245 + 12345) >>> 0;
    // Bar/gap strictly alternates by position (i % 2), not by a hash-parity
    // coin flip -- the coin flip was the actual bug: random bits often repeat
    // several times in a row, which merges runs of same-colored segments into
    // a few wide bars instead of 48 distinct ones. The hash still drives each
    // bar's individual width (30%-90% of its slot) so it doesn't look like a
    // perfectly uniform ruler.
    const isBar = i % 2 === 0;
    const color = isBar ? "#0a0a0a" : "transparent";
    const widthFrac = 0.3 + (hash % 61) / 100; // 0.30 .. 0.90
    const start = i * segmentWidth;
    const end = isBar ? start + segmentWidth * widthFrac : start + segmentWidth;
    stops.push(`${color} ${start}%`, `${color} ${end}%`);
  }
  // Plain (non-repeating) gradient -- all 48 segments' stops are already
  // emitted explicitly above.
  return `linear-gradient(90deg, ${stops.join(", ")})`;
}

// Formats a debt/balance number the same way the game's own card does:
// thousands-separated, 2 decimal places, no currency symbol prefix (the
// card's own layout implies $ via context, matching the in-game screenshots).
function formatDebtAmount(n) {
  return n.toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

function renderLynxCard(profile) {
  if (!profile) {
    return el("div", { className: "lynx-card" }, el("div", { className: "lynx-card-empty" }, "Select a save to view details."));
  }
  const debt = profile.debt;
  const debtLabel = debt === null ? "Debt" : debt.isPaidOff ? "Balance" : "Debt";
  const debtValue = debt === null ? "Unknown" : formatDebtAmount(debt.amount);

  return el("div", { className: "lynx-card" }, [
    el("div", { className: "lynx-card-header" }, [el("span", { className: "lynx-pawmark", html: "\u{1F43E}" }), "LYNX CORP."]),
    el("div", { className: "lynx-card-body" }, [
      el("div", { className: "lynx-badge", html: LYNX_SKULL_SVG }),
      el("div", { className: "lynx-fields" }, [
        el("div", {}, [el("div", { className: "lynx-field-label" }, "Name"), el("div", { className: "lynx-field-value" }, profile.profileName.toUpperCase())]),
        el("div", {}, [el("div", { className: "lynx-field-label" }, "Difficulty"), el("div", { className: "lynx-field-value" }, profile.difficultyMode ?? "Unknown")]),
        el("div", {}, [el("div", { className: "lynx-field-label" }, "Rank"), el("div", { className: "lynx-field-value" }, String(profile.rank ?? "?"))]),
        el("div", {}, [el("div", { className: "lynx-field-label" }, debtLabel), el("div", { className: "lynx-field-value mono" }, debtValue)]),
      ]),
    ]),
    el("div", { className: "lynx-barcode", style: `background-image: ${barcodeGradient(profile.fileName)}` }),
  ]);
}

async function renderProfilePicker() {
  app.replaceChildren(el("div", {}, "Loading profiles..."));
  let profiles;
  try {
    profiles = await api("/profiles");
  } catch (err) {
    app.replaceChildren(renderError(err));
    return;
  }

  const cardSlot = el("div", {}, renderLynxCard(profiles[0] ?? null));

  const menu = el("div", { className: "lynx-menu" });
  if (profiles.length === 0) {
    menu.append(
      el("div", { className: "empty-state" }, [
        el("p", {}, "No saves found in your Saves\\Profiles folder."),
        el("p", {}, "If your save was shared with you as a file, use Browse below."),
      ]),
    );
  } else {
    profiles.forEach((p, i) => {
      const row = el(
        "div",
        { className: `lynx-row ${i === 0 ? "selected" : ""}` },
        [
          el("span", { className: "lynx-mode-icon" }, (p.difficultyMode ?? "?")[0] ?? "?"),
          p.profileName,
          el("span", { className: "lynx-row-sub" }, `Rank ${p.rank ?? "?"}`),
        ],
      );
      row.addEventListener("mouseenter", () => cardSlot.replaceChildren(renderLynxCard(p)));
      row.addEventListener("click", () => openProfile(p.id));
      menu.append(row);
    });
  }

  const browseRow = el("div", { className: "lynx-browse-row" }, [
    el("input", { type: "text", id: "browse-path", placeholder: "Or paste a full path to a .lpw file..." }),
    el("button", { onClick: browseFile }, "Browse"),
  ]);

  app.replaceChildren(
    el("div", {}, [
      el("div", { className: "lynx-picker" }, [menu, cardSlot]),
      browseRow,
    ]),
  );
}

async function browseFile() {
  const input = document.getElementById("browse-path");
  const path = input.value.trim();
  if (!path) return;
  try {
    const profile = await api("/profiles/browse", { method: "POST", body: JSON.stringify({ path }) });
    openProfile(profile.id);
  } catch (err) {
    app.append(renderError(err));
  }
}

function openProfile(id) {
  currentProfileId = id;
  renderSaveDetail();
}

// ---------------------------------------------------------------------------
// Screen 2: save detail (milestone checklist + difficulty mode)
// ---------------------------------------------------------------------------

// Turn a raw internal PAT name into a readable label, e.g.
// "PAT_CMP_11_01_CrewPrivateComms_Night_Complete" -> "Crew Private Comms - Night".
// Strips the "PAT_CMP_<rank>_<seq>_" prefix (also handles the "A3_"/"X<n>_"
// Act-3 variants) and the trailing "_Complete"/"_ScenePlayed" status suffix,
// then turns remaining underscores into " - " and splits CamelCase words.
function humanizeMilestoneName(raw) {
  let s = raw.replace(/^PAT_CMP_/, "");
  // Strip the rank/sequence prefix: "02_00_", "17_X2_", "A3_SC01_" all boil
  // down to <rank-token>_<optional-letter-code>_<optional-numeric-seq>_.
  s = s.replace(/^(?:\d+|A3)_(?:[A-Z0-9]+_)?(?:\d+_)?/, "");
  s = s.replace(/_(Complete|ScenePlayed|Read|Tracker)$/, "");
  return s
    .split("_")
    .map((part) => part.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/([A-Z]+)([A-Z][a-z])/g, "$1 $2"))
    .join(" - ");
}

function milestoneRow(m, isCurrent) {
  // The current-position row is the save's furthest-reached point, not
  // necessarily a completed one (it can be the entry right after a gap) --
  // its dot always reads as "not complete" (grey), and it has no action
  // button at all, since there's nothing to go back from or forward to here.
  const dotClass = isCurrent ? "dot-incomplete" : m.status === "gap" ? "dot-gap" : m.status === "complete" ? "dot-complete" : "dot-incomplete";
  const rowClasses = ["milestone-row", `status-${m.status}`];
  if (isCurrent) rowClasses.push("current-position");

  let button = null;
  if (!isCurrent) {
    if (m.status === "complete") {
      button = el("button", { onClick: () => confirmGotoMilestone(m.name) }, "Go Back");
    } else if (m.status === "gap") {
      button = el("button", { onClick: () => confirmCompleteMissing(m.name) }, "Complete Missing");
    } else {
      button = el("button", { onClick: () => confirmFillMilestone(m.name) }, "Go Forward");
    }
  }

  return el("div", { className: rowClasses.join(" ") }, [
    el("span", { className: `dot ${dotClass}` }),
    el("span", { className: "name" }, [
      el("span", { className: "name-human" }, humanizeMilestoneName(m.name)),
      el("span", { className: "name-raw" }, m.name),
    ]),
    button,
  ]);
}

function renderMilestoneGroups(milestones, saveRank) {
  const groups = [
    { key: "main", label: null }, // main-chain rank groups are labeled per-rank below
    { key: "act3", label: "Act 3" },
  ];

  const wrap = el("div", { className: "milestone-groups" });

  for (const { key, label } of groups) {
    const entries = milestones.filter((m) => m.group === key);
    if (entries.length === 0) continue;

    // Find this group's current position. Prefer the save's actual RANK
    // (from CertificationTierData) over "last complete PAT": after a "Go
    // Back", the target milestone's own PAT is deliberately stripped (so it
    // retriggers) but rank is deliberately left at the target's rank -- using
    // last-complete alone would highlight one entry further back than the
    // save is actually sitting at. Fall back to last-complete if rank is
    // unknown or doesn't match any entry (e.g. Act-3, which has no useful
    // per-entry rank granularity of its own).
    let currentIdx = -1;
    if (typeof saveRank === "number") {
      entries.forEach((m, i) => {
        if (m.rank <= saveRank) currentIdx = i;
      });
    }
    if (currentIdx === -1) {
      entries.forEach((m, i) => {
        if (m.status === "complete") currentIdx = i;
      });
    }

    if (label) {
      wrap.append(el("h2", { style: "margin-top:24px" }, label));
    }

    // Sub-group consecutive entries sharing the same rank into their own
    // outlined block, per rank, in chain order.
    let currentRank = null;
    let rankBlock = null;
    entries.forEach((m, i) => {
      if (m.rank !== currentRank) {
        currentRank = m.rank;
        rankBlock = el("div", { className: "rank-group" }, [el("div", { className: "rank-group-label" }, `Rank ${m.rank}`)]);
        wrap.append(rankBlock);
      }
      rankBlock.append(milestoneRow(m, i === currentIdx));
    });
  }

  return wrap;
}

// Shared profile summary card -- used at the top of both the save-detail
// screen and Advanced mode, so users can scroll a long JSON tree and still
// see which profile they're editing without going back.
function renderProfileHeader(detail, buttons) {
  const debt = detail.debt;
  const debtText = debt === null ? "Debt Unknown" : `${debt.isPaidOff ? "Balance" : "Debt"} $${formatDebtAmount(debt.amount)}`;
  return el("div", { className: "card" }, [
    el("div", { className: "profile-row" }, [
      el("div", {}, [
        el("div", { className: "profile-name" }, detail.profileName ?? "(unknown profile)"),
        el(
          "div",
          { className: "profile-meta" },
          `Rank ${detail.rank ?? "?"} · XP ${detail.xp ?? "?"} · ${detail.difficultyMode ?? "Unknown mode"} · ${debtText}`,
        ),
      ]),
      el("div", { className: "btn-row", style: "margin-top:0" }, buttons),
    ]),
  ]);
}

async function renderSaveDetail() {
  app.replaceChildren(el("div", {}, "Loading save..."));
  let detail;
  try {
    detail = await api(`/save/${currentProfileId}`);
  } catch (err) {
    app.replaceChildren(renderError(err));
    return;
  }

  const header = renderProfileHeader(detail, [
    el("button", { onClick: renderBackupList }, "View backups"),
    el("button", { onClick: renderAdvancedEditor }, "Advanced mode"),
  ]);

  // "Go Back" (goto-milestone): only makes sense on a milestone the save has
  // already completed -- rolls it back so it retriggers. "Go Forward"
  // (fill-milestone): only makes sense on a milestone not yet completed --
  // fills in the prerequisites and marks it done. Status is one of "complete"
  // / "gap" / "incomplete" (see buildMilestoneChecklist on the backend):
  // "gap" means a LATER milestone is complete but this one isn't -- a real
  // anomaly (the exact class of bug this tool exists to fix), shown with a
  // red dot. Plain "incomplete" (nothing later is complete either -- just not
  // reached yet) gets a grey dot. The last complete entry in each group is
  // highlighted as the save's current position in that chain.
  const milestoneList = renderMilestoneGroups(detail.milestones, detail.rank);

  const modeList = el(
    "div",
    { className: "mode-list" },
    (await api("/difficulty-modes")).map((mode) =>
      el(
        "div",
        { className: `mode-row ${mode.name === detail.difficultyMode ? "current" : ""}` },
        [
          el("span", {}, mode.name),
          mode.name === detail.difficultyMode
            ? el("span", { className: "pill mode-known" }, "Current")
            : el("button", { onClick: () => confirmSetDifficultyMode(mode.name) }, "Switch to this"),
        ],
      ),
    ),
  );

  app.replaceChildren(
    el("div", {}, [
      backLink("Back to profiles", renderProfilePicker),
      header,
      el("h2", {}, "Difficulty mode"),
      el(
        "div",
        { className: "notice" },
        "The game only allows one active save per difficulty mode at a time -- if another of your saves already uses the mode you switch to, you'll be warned after the change.",
      ),
      modeList,
      el("h2", {}, "Story milestones"),
      el(
        "div",
        { className: "notice" },
        "Go Back removes a completed milestone (and everything after it) so it retriggers on next load. Go Forward fills in the prerequisites for an incomplete milestone and marks it done. A backup is made automatically before any change.",
      ),
      milestoneList,
    ]),
  );
}

// ---------------------------------------------------------------------------
// Screen 2b: backup list (view/restore any backup this tool has made)
// ---------------------------------------------------------------------------

async function renderBackupList() {
  app.replaceChildren(el("div", {}, "Loading backups..."));
  let backups;
  try {
    backups = await api(`/save/${currentProfileId}/backups`);
  } catch (err) {
    app.replaceChildren(renderError(err));
    return;
  }

  const rows =
    backups.length === 0
      ? [el("div", { className: "empty-state" }, "No backups yet -- one is made automatically before any change.")]
      : backups.map((b) => {
          const restoreBtn = el("button", { className: "danger" }, "Restore this");
          restoreBtn.addEventListener("click", async () => {
            if (!confirm(`Restore the backup from ${b.timestamp}?\n\nReason logged: ${b.reason}\n\nThis overwrites the current live save (which is itself backed up first).`)) {
              return;
            }
            restoreBtn.disabled = true;
            restoreBtn.textContent = "Restoring...";
            try {
              await api(`/save/${currentProfileId}/restore-backup`, {
                method: "POST",
                body: JSON.stringify({ backupPath: b.path }),
              });
              renderSaveDetail();
            } catch (err) {
              restoreBtn.disabled = false;
              restoreBtn.textContent = "Restore this";
              alert(`Restore failed: ${err.message}`);
            }
          });
          return el("div", { className: "card" }, [
            el("div", { className: "profile-row" }, [
              el("div", {}, [
                el("div", { className: "profile-name" }, b.timestamp),
                el("div", { className: "profile-meta" }, b.reason),
              ]),
              restoreBtn,
            ]),
          ]);
        });

  app.replaceChildren(
    el("div", {}, [
      backLink("Back to save", renderSaveDetail),
      el("h2", { style: "margin-top:0" }, "Backups"),
      el(
        "div",
        { className: "notice" },
        "Every change this tool makes backs up the file first, newest first below. Restoring one also backs up the current state before overwriting it, so nothing is ever lost.",
      ),
      ...rows,
    ]),
  );
}

// ---------------------------------------------------------------------------
// Screen 3: confirm + apply
// ---------------------------------------------------------------------------

function confirmGotoMilestone(milestoneName) {
  renderConfirm({
    title: "Roll back story progress?",
    body: `This will roll your save back to just before "${milestoneName}" so it retriggers on next load. Everything after it in the story chain will be removed. A backup will be made automatically first.`,
    onConfirm: async () => {
      const resp = await api(`/save/${currentProfileId}/goto-milestone`, {
        method: "POST",
        body: JSON.stringify({ milestone: milestoneName }),
      });
      renderGotoMilestoneResult(resp);
    },
  });
}

function confirmFillMilestone(milestoneName) {
  renderConfirm({
    title: "Advance story progress?",
    body: `This will fill in every prerequisite before "${milestoneName}" (copied from a fully-completed reference save) and mark it done, moving your rank forward to match. A backup will be made automatically first.`,
    onConfirm: async () => {
      const resp = await api(`/save/${currentProfileId}/fill-milestone`, {
        method: "POST",
        body: JSON.stringify({ milestone: milestoneName }),
      });
      renderFillMilestoneResult(resp);
    },
  });
}

function confirmCompleteMissing(milestoneName) {
  renderConfirm({
    title: "Complete this missing milestone?",
    body: `Your save has already moved past "${milestoneName}" without ever recording it -- this fills in just that one milestone (copied from a fully-completed reference save) without touching rank, XP, or anything else. A backup will be made automatically first.`,
    onConfirm: async () => {
      const resp = await api(`/save/${currentProfileId}/complete-missing`, {
        method: "POST",
        body: JSON.stringify({ milestone: milestoneName }),
      });
      renderCompleteMissingResult(resp);
    },
  });
}

function confirmSetDifficultyMode(modeName) {
  renderConfirm({
    title: "Change difficulty mode?",
    body: `This will switch your save to ${modeName} mode. A backup will be made automatically first.`,
    onConfirm: async () => {
      const resp = await api(`/save/${currentProfileId}/set-difficulty-mode`, {
        method: "POST",
        body: JSON.stringify({ mode: modeName }),
      });
      renderDifficultyModeResult(resp);
    },
  });
}

function renderConfirm({ title, body, onConfirm }) {
  const errorSlot = el("div", {});
  const confirmBtn = el("button", { className: "primary" }, "Apply");
  confirmBtn.addEventListener("click", async () => {
    confirmBtn.disabled = true;
    confirmBtn.textContent = "Applying...";
    try {
      await onConfirm();
    } catch (err) {
      confirmBtn.disabled = false;
      confirmBtn.textContent = "Apply";
      errorSlot.replaceChildren(renderError(err));
    }
  });

  app.replaceChildren(
    el("div", {}, [
      backLink("Cancel", renderSaveDetail),
      el("div", { className: "card" }, [el("h2", { style: "margin-top:0" }, title), el("p", {}, body)]),
      errorSlot,
      el("div", { className: "btn-row" }, [confirmBtn, el("button", { onClick: renderSaveDetail }, "Cancel")]),
    ]),
  );
}

// ---------------------------------------------------------------------------
// Screen 4: result (diff summary + restore-last-backup)
// ---------------------------------------------------------------------------

function diffList(className, label, entries, format) {
  if (entries.length === 0) return null;
  return el("div", { className: "diff-section" }, [
    el("div", {}, `${label} (${entries.length}):`),
    el(
      "ul",
      { className: `diff-list ${className}` },
      entries.map((e) => el("li", {}, format(e))),
    ),
  ]);
}

function renderGotoMilestoneResult({ result, diff }) {
  const summary = [];
  if (diff.rankChanged) summary.push(el("p", {}, `Rank: ${diff.rankChanged[0]} -> ${diff.rankChanged[1]}`));
  if (diff.xpChanged) summary.push(el("p", {}, `XP: ${diff.xpChanged[0]} -> ${diff.xpChanged[1]} (${result.xpNote})`));

  summary.push(diffList("removed", "Milestones removed", diff.actionTrackerRemoved, ([k]) => k));
  summary.push(diffList("removed", "Messages removed (will re-arrive if relevant)", diff.messageRemoved, (k) => k));

  app.replaceChildren(renderResultScreen("Done", "Your save now retriggers this story beat on next load.", summary));
}

function renderFillMilestoneResult({ result, diff }) {
  const summary = [];
  if (diff.rankChanged) summary.push(el("p", {}, `Rank: ${diff.rankChanged[0]} -> ${diff.rankChanged[1]}`));
  if (diff.xpChanged) summary.push(el("p", {}, `XP: ${diff.xpChanged[0]} -> ${diff.xpChanged[1]} (${result.xpNote})`));

  summary.push(diffList("added", "Milestones filled in", diff.actionTrackerAdded, ([k]) => k));
  summary.push(diffList("added", "Messages added", diff.messageAdded, (k) => k));

  app.replaceChildren(renderResultScreen("Done", "Your save now has this story beat marked complete.", summary));
}

function renderCompleteMissingResult({ result, diff }) {
  const summary = [];
  summary.push(diffList("added", "Milestone filled in", diff.actionTrackerAdded, ([k]) => k));
  summary.push(diffList("added", "Message added", diff.messageAdded, (k) => k));

  app.replaceChildren(renderResultScreen("Done", "This milestone is now marked complete. Go back to the save to see it update.", summary));
}

function renderDifficultyModeResult({ mode, warning }) {
  const summary = [el("p", {}, `Difficulty mode set to ${mode}.`)];
  if (warning) summary.push(el("div", { className: "notice warning" }, warning));

  app.replaceChildren(renderResultScreen("Done", "Difficulty mode updated.", summary));
}

function renderResultScreen(title, subtitle, bodyChildren) {
  const restoreBtn = el("button", { className: "danger" }, "Restore last backup");
  restoreBtn.addEventListener("click", async () => {
    restoreBtn.disabled = true;
    restoreBtn.textContent = "Restoring...";
    try {
      const restored = await api(`/save/${currentProfileId}/restore-backup`, { method: "POST" });
      alert(`Restored backup from ${restored.timestamp} (${restored.reason}).`);
      renderSaveDetail();
    } catch (err) {
      restoreBtn.disabled = false;
      restoreBtn.textContent = "Restore last backup";
      alert(`Restore failed: ${err.message}`);
    }
  });

  return el("div", {}, [
    el("div", { className: "card" }, [el("h2", { style: "margin-top:0" }, title), el("p", {}, subtitle), ...bodyChildren]),
    el("div", { className: "btn-row" }, [el("button", { className: "primary", onClick: renderSaveDetail }, "Back to save"), restoreBtn]),
  ]);
}

// ---------------------------------------------------------------------------
// Screen 5: advanced mode -- collapsible JSON tree editor over the same
// decoded fields the rest of the UI edits (generalData/certification/
// actionTracker/messageHistory/difficultyMode), plus a read-only view of
// randomSceneHistory. Editing happens on a live in-memory copy of the parsed
// JSON (not raw text re-parsed on every keystroke); Save sends the whole
// object to the backend, which re-validates, backs up, and re-encodes.
// ---------------------------------------------------------------------------

// A node defaults open if it has FEWER THAN 8 direct children (its own
// object keys / array entries, not counting anything nested further down),
// collapsed otherwise.
const ADVANCED_COLLAPSE_CHILD_THRESHOLD = 4;

// Builds an editable tree node for `value` under `key` (its label as shown
// in the parent), calling `onChange(newValue)` whenever a leaf edit should
// be applied back onto the parent's copy of the data. `path` is the dotted
// key path from the root, used for search matching and auto-expanding
// ancestors of a search hit.
//
// A container's key + expand arrow + entry count are ALWAYS one single
// clickable <summary> line ("▸ messageHistory { 2 entries }") -- used
// uniformly for the top-level fields and every nested container, so there
// is exactly one layout rule for "how a container renders" rather than a
// separate special case for the root level.
//
// Only LEAF entries (scalar values -- no children) get a remove button.
// Container entries (nested object/array, e.g. certification.tiers or one
// message's own sub-fields) never get one on their own row: "remove this
// scalar PAT/timestamp" is unambiguous, but "remove this whole sub-object"
// is not a single well-defined action here -- to delete several fields at
// once, remove each of that container's own leaf children individually.
function advancedContainerNode(key, value, onChange, path, searchState) {
  const isArray = Array.isArray(value);
  const entries = isArray ? value.map((v, i) => [String(i), v]) : Object.entries(value);
  const count = entries.length;

  const details = el("details", { className: "adv-node" });
  details.open = count < ADVANCED_COLLAPSE_CHILD_THRESHOLD;
  searchState.nodes.push({ path, details });

  const summary = el("summary", {}, [
    el("span", { className: "adv-key", "data-search-key": path.toLowerCase() }, String(key)),
    el("span", { className: "adv-node-count" }, ` ${isArray ? "[" : "{"} ${count} ${count === 1 ? "entry" : "entries"} ${isArray ? "]" : "}"}`),
  ]);
  details.append(summary);

  // Scalar-valued children (regardless of whether a SIBLING key holds a
  // nested object, e.g. certification.tiers next to rank/xp) are laid out as
  // the body's own CSS grid (.adv-leaf-grid) so their value inputs align in
  // one column, sized to THIS body's own widest scalar value only (not
  // shared across sections, and not thrown off by the one non-scalar sibling).
  const scalarEntries = entries.filter(([, v]) => v === null || typeof v !== "object");
  const widestLen = scalarEntries.length > 0 ? Math.max(4, ...scalarEntries.map(([, v]) => String(v).length)) : 4;
  const body = el("div", { className: "adv-node-body adv-leaf-grid" });

  entries.forEach(([childKey, childValue], i) => {
    const childPath = path ? `${path}.${childKey}` : childKey;
    const isContainer = childValue !== null && typeof childValue === "object";
    const childOnChange = (newChildValue) => {
      if (isArray) value[Number(childKey)] = newChildValue;
      else value[childKey] = newChildValue;
      onChange(value);
    };
    const stripeClass = i % 2 === 1 ? " adv-row-odd" : "";
    const displayKey = isArray ? `[${childKey}]` : childKey;

    if (isContainer) {
      // No remove button here -- see the function-level comment above.
      const row = el("div", { className: `adv-row adv-row-container${stripeClass}` });
      row.append(advancedContainerNode(displayKey, childValue, childOnChange, childPath, searchState));
      body.append(row);
    } else {
      const childOnRemove = () => {
        if (isArray) value.splice(Number(childKey), 1);
        else delete value[childKey];
        onChange(value);
        searchState.onTreeChanged();
      };
      const row = el("div", { className: `adv-row adv-row-leaf${stripeClass}` }, [
        el("span", { className: "adv-key", "data-search-key": childPath.toLowerCase() }, displayKey),
        el("span", { className: "adv-value-cell" }, advancedLeafNode(childValue, childOnChange, widestLen)),
        removeButton(childOnRemove),
      ]);
      body.append(row);
    }
  });
  details.append(body);
  return details;
}

function removeButton(onRemove) {
  return el("button", { className: "adv-remove-btn", title: "Remove this entry", onClick: onRemove }, "×");
}

// `widestLen` sizes the input's `size` attribute so a whole leaf-grid body's
// value column hugs its own widest value (e.g. an 8-digit hash vs. "No
// Revives") instead of sharing one width across unrelated sections.
function advancedLeafNode(value, onChange, widestLen) {
  if (typeof value === "boolean") {
    const input = el("input", { type: "checkbox", className: "adv-leaf-checkbox" });
    input.checked = value;
    input.addEventListener("change", () => onChange(input.checked));
    return input;
  }
  if (typeof value === "number") {
    const input = el("input", { type: "text", className: "adv-leaf-input mono", value: String(value), size: String(widestLen) });
    input.addEventListener("change", () => {
      const n = Number(input.value);
      onChange(Number.isNaN(n) ? input.value : n);
    });
    return input;
  }
  // Strings (including bigint-as-string timestamps) and any other scalar.
  const input = el("input", { type: "text", className: "adv-leaf-input mono", value: String(value), size: String(widestLen) });
  input.addEventListener("change", () => onChange(input.value));
  return input;
}

function advancedSearchFilter(searchState, query) {
  const q = query.trim().toLowerCase();
  for (const { path, details } of searchState.nodes) {
    if (q === "") {
      details.classList.remove("adv-hidden");
      continue;
    }
    const matches = path.toLowerCase().includes(q);
    details.classList.toggle("adv-hidden", false);
    if (matches) details.open = true;
  }
  // Highlight matching key labels; expand ancestor chains of any match.
  const keyEls = app.querySelectorAll(".adv-key");
  let anyMatch = q === "";
  keyEls.forEach((el) => {
    const key = el.dataset.searchKey ?? "";
    const isMatch = q !== "" && key.includes(q);
    el.classList.toggle("adv-key-match", isMatch);
    if (isMatch) {
      anyMatch = true;
      let node = el.closest(".adv-node");
      while (node) {
        node.open = true;
        node = node.parentElement?.closest(".adv-node") ?? null;
      }
    }
  });
  return anyMatch;
}

async function renderAdvancedEditor() {
  app.replaceChildren(el("div", {}, "Loading advanced editor..."));
  let data, detail;
  try {
    [data, detail] = await Promise.all([api(`/save/${currentProfileId}/advanced`), api(`/save/${currentProfileId}`)]);
  } catch (err) {
    app.replaceChildren(renderError(err));
    return;
  }

  // Working copy -- mutated in place by leaf edits, sent to the backend as-is
  // on Save. Cancel simply discards this object and re-renders the save.
  const working = JSON.parse(JSON.stringify(data));

  const searchInput = el("input", { type: "text", placeholder: "Search field names...", className: "adv-search" });
  const noMatchNotice = el("div", { className: "notice", style: "display:none" }, "No fields match your search.");
  const treeRoot = el("div", { className: "adv-tree" });

  const searchState = {
    nodes: [],
    onTreeChanged: () => {
      const query = searchInput.value;
      buildTree();
      const anyMatch = advancedSearchFilter(searchState, query);
      noMatchNotice.style.display = anyMatch ? "none" : "block";
    },
  };

  function buildTree() {
    searchState.nodes = [];
    treeRoot.replaceChildren();
    for (const [key, value] of Object.entries(working)) {
      const onChange = (newValue) => { working[key] = newValue; };
      if (value === null) {
        treeRoot.append(
          el("div", { className: "adv-row adv-row-top" }, [
            el("span", { className: "adv-key", "data-search-key": key.toLowerCase() }, key),
            el("span", { className: "adv-null" }, " null"),
          ]),
        );
      } else if (typeof value !== "object") {
        // A top-level scalar (voiceData, oxygenDrainData, foodChoiceData) --
        // not a container, so no "{ N entries }" collapsible wrapper; just a
        // labeled row with a direct edit input, same as any leaf row.
        treeRoot.append(
          el("div", { className: "adv-row adv-row-top adv-row-top-leaf" }, [
            el("span", { className: "adv-key", "data-search-key": key.toLowerCase() }, key),
            el("span", { className: "adv-value-cell" }, advancedLeafNode(value, onChange, String(value).length)),
          ]),
        );
      } else {
        treeRoot.append(advancedContainerNode(key, value, onChange, key, searchState));
      }
    }
  }
  buildTree();

  searchInput.addEventListener("input", () => {
    const anyMatch = advancedSearchFilter(searchState, searchInput.value);
    noMatchNotice.style.display = anyMatch ? "none" : "block";
  });

  const errorSlot = el("div", {});
  const saveBtn = el("button", { className: "primary" }, "Save");
  saveBtn.addEventListener("click", async () => {
    saveBtn.disabled = true;
    saveBtn.textContent = "Checking & saving...";
    errorSlot.replaceChildren();
    try {
      await api(`/save/${currentProfileId}/advanced`, {
        method: "POST",
        body: JSON.stringify({ data: working }),
      });
      renderSaveDetail();
    } catch (err) {
      saveBtn.disabled = false;
      saveBtn.textContent = "Save";
      errorSlot.replaceChildren(renderError(err));
    }
  });
  const cancelBtn = el("button", { onClick: renderSaveDetail }, "Cancel");

  app.replaceChildren(
    el("div", {}, [
      backLink("Cancel", renderSaveDetail),
      renderProfileHeader(detail, []),
      el("h2", { style: "margin-top:0" }, "Advanced mode"),
      el(
        "div",
        { className: "notice warning" },
        "Editing raw save fields directly. Invalid values are rejected before anything is written. A backup is still made automatically before saving. randomSceneHistory is read-only (shown for visibility only).",
      ),
      searchInput,
      noMatchNotice,
      treeRoot,
      errorSlot,
      el("div", { className: "btn-row" }, [saveBtn, cancelBtn]),
    ]),
  );
}

// ---------------------------------------------------------------------------

renderProfilePicker();
