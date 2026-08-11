export interface OrderedListNumberingState {
  lastNumber: number | null;
  previousWasOrdered: boolean;
}

export function resolveOrderedListNumber(
  sequence: string | null | undefined,
  state: OrderedListNumberingState
): number {
  const normalized = sequence?.trim().toLowerCase() ?? "";
  if (/^[0-9]+$/.test(normalized)) {
    const explicit = Number(normalized);
    if (Number.isSafeInteger(explicit) && explicit > 0) {
      return explicit;
    }
  }
  if (normalized === "auto") {
    return (state.lastNumber ?? 0) + 1;
  }
  if (state.previousWasOrdered && state.lastNumber !== null) {
    return state.lastNumber + 1;
  }
  return 1;
}
