/**
 * Minimal line-based diff used to highlight the lines an agent edit touched.
 *
 * Returns the 1-based line numbers in `newText` that differ from `oldText`
 * (lines that were added or whose content changed). A "modified" line is
 * reported as a changed line because it appears as an old-line removal plus a
 * new-line insertion. Uses a longest-common-subsequence pass over lines; for
 * very large files it falls back to a cheap prefix comparison to stay fast.
 */

const LCS_CELL_BUDGET = 2_000_000; // avoid O(n*m) blow-up on huge files

export function changedLineNumbers(oldText: string, newText: string): number[] {
  const oldLines = oldText.split('\n');
  const newLines = newText.split('\n');

  if (oldLines.length === 0) return newLines.map((_, i) => i + 1);
  if (newLines.length === 0) return [];

  if (oldLines.length * newLines.length > LCS_CELL_BUDGET) {
    return cheapDiff(oldLines, newLines);
  }

  const n = oldLines.length;
  const m = newLines.length;

  const dp: number[][] = Array.from({ length: n + 1 }, () => new Array<number>(m + 1).fill(0));
  for (let i = n - 1; i >= 0; i--) {
    for (let j = m - 1; j >= 0; j--) {
      dp[i][j] =
        oldLines[i] === newLines[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
    }
  }

  const changed = new Set<number>();
  let i = 0;
  let j = 0;
  while (i < n && j < m) {
    if (oldLines[i] === newLines[j]) {
      i++;
      j++;
    } else if (dp[i + 1][j] >= dp[i][j + 1]) {
      i++; // line removed from old
    } else {
      changed.add(j + 1); // line added in new
      j++;
    }
  }
  while (j < m) {
    changed.add(j + 1);
    j++;
  }

  return Array.from(changed).sort((a, b) => a - b);
}

function cheapDiff(oldLines: string[], newLines: string[]): number[] {
  const changed: number[] = [];
  const shared = Math.min(oldLines.length, newLines.length);
  for (let i = 0; i < shared; i++) {
    if (oldLines[i] !== newLines[i]) changed.push(i + 1);
  }
  for (let i = shared; i < newLines.length; i++) changed.push(i + 1);
  return changed;
}
