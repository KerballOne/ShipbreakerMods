/**
 * computeDebt() against the 4 real in-game debt/balance screenshots captured
 * 2026-07-10 (Standard/test5, Open Shift/BTest1, Limited/NEVARMOAR, No
 * Revives/heidi) -- see debt.ts docstring for the full confirmation writeup.
 */
import { test } from "node:test";
import * as assert from "node:assert/strict";
import { computeDebt, STARTING_DEBT_AMOUNT } from "../src/lpw/debt";

test("STARTING_DEBT_AMOUNT matches the confirmed game-wide constant", () => {
  assert.equal(STARTING_DEBT_AMOUNT, 1252594441.92);
});

test("computeDebt: Standard mode (test5) matches the real screenshot", () => {
  const result = computeDebt(4118542.75);
  assert.ok(result);
  assert.equal(Math.round(result.amount * 100) / 100, 1248475899.17);
  assert.equal(result.isPaidOff, false);
});

test("computeDebt: Limited mode (NEVARMOAR) matches the real screenshot", () => {
  const result = computeDebt(25489428.0);
  assert.ok(result);
  assert.equal(Math.round(result.amount * 100) / 100, 1227105013.92);
  assert.equal(result.isPaidOff, false);
});

test("computeDebt: No Revives mode (heidi) matches the real screenshot", () => {
  const result = computeDebt(507650176.0);
  assert.ok(result);
  assert.equal(Math.round(result.amount * 100) / 100, 744944265.92);
  assert.equal(result.isPaidOff, false);
});

test("computeDebt: Open Shift mode (BTest1) is paid off -- shows Balance, matches the real screenshot", () => {
  const result = computeDebt(1252894080.0);
  assert.ok(result);
  assert.equal(Math.round(result.amount * 100) / 100, 299638.08);
  assert.equal(result.isPaidOff, true);
});

test("computeDebt returns null when Credits amount is unavailable", () => {
  assert.equal(computeDebt(null), null);
});
