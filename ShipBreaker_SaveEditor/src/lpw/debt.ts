/**
 * Debt is NOT stored in the .lpw save at all -- confirmed 2026-07-10 from
 * decompiled source (CurrencyUIController.UpdateElement): the game computes
 * it live as `DifficultyMode.StartingDebtAmount - Credits.Amount`, where
 * Credits is CurrencyController.Currencies[Credits_CurrencyAsset] (already
 * decoded via sections.ts decodeCurrencyData). If the result is <= 0, the
 * game flips the label from "Debt" to "Balance" and shows the absolute value
 * (debt fully paid off, player is in profit).
 *
 * STARTING_DEBT_AMOUNT was independently confirmed identical across all 4
 * difficulty modes via live in-game captures (PartInfoLogger's
 * profileFieldDump), cross-checked against real in-game debt/balance
 * screenshots for each mode -- Standard, Open Shift ("Casual" internally),
 * Limited ("LimitedRevival" internally), and No Revives ("NoRevival"
 * internally) all read exactly 1252594441.92. It is a single game-wide
 * constant, not truly per-mode, despite living on DifficultyModeAsset.
 */
export const STARTING_DEBT_AMOUNT = 1252594441.92;

export interface DebtStatus {
  /** Positive if debt remains, negative/zero if paid off (see isPaidOff). */
  amount: number;
  isPaidOff: boolean;
}

/** Computes the live debt/balance display value from a save's Credits
 * amount, or null if CurrencyData wasn't decoded (e.g. this section is
 * absent from an unusual save). */
export function computeDebt(creditsAmount: number | null): DebtStatus | null {
  if (creditsAmount === null) return null;
  const raw = STARTING_DEBT_AMOUNT - creditsAmount;
  return { amount: Math.abs(raw), isPaidOff: raw <= 0 };
}
