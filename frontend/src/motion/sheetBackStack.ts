/**
 * SHEET_BACK_STACK
 * Единый стек открытых BottomSheet: системный Back закрывает верхний лист,
 * а не приложение и не экран под ним.
 */
type CloseHandler = () => void;

const stack: CloseHandler[] = [];

export function registerSheetBack(onClose: CloseHandler): () => void {
  stack.push(onClose);
  return () => {
    const index = stack.lastIndexOf(onClose);
    if (index >= 0) stack.splice(index, 1);
  };
}

/** Закрывает верхний лист. true — слой был и обработан. */
export function closeTopSheet(): boolean {
  const handler = stack[stack.length - 1];
  if (!handler) return false;
  handler();
  return true;
}

export function hasOpenSheet(): boolean {
  return stack.length > 0;
}
