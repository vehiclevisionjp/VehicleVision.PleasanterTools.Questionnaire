/**
 * 確認ダイアログに添えるチェック（Issue #1025。「この人の API キーも失効させる」）。
 * **初期値は呼び出し側が決める**（失効させる側へ倒すなら `checked: true`）。
 */
export interface ConfirmationCheckbox {
  label: string;
  checked: boolean;
}

export interface ConfirmationOptions {
  title: string;
  message: string;
  confirmLabel: string;
  danger?: boolean;
  /** 初期の focus をキャンセルへ置く（取り返しのつかない削除。DesignReference の確認 dialog の契約）。 */
  focusCancel?: boolean;
  checkbox?: ConfirmationCheckbox;
}

export interface ConfirmationRequest extends ConfirmationOptions {
  id: number;
}

/** チェックつきの確認の結果。`checked` は確認した時点のチェックの状態。 */
export interface ConfirmationResult {
  confirmed: boolean;
  checked: boolean;
}

interface FocusTarget {
  focus(): void;
}

interface ConfirmationKeyEvent {
  key: string;
  shiftKey: boolean;
  preventDefault(): void;
}

interface PendingConfirmation extends ConfirmationRequest {
  resolve(result: ConfirmationResult): void;
  returnFocusTo: FocusTarget | null;
}

let nextId = 0;
let pending = $state<PendingConfirmation | null>(null);

export function currentConfirmation(): ConfirmationRequest | null {
  return pending;
}

export function confirmAction(options: ConfirmationOptions): Promise<boolean> {
  return confirmActionWithChoice(options).then((result) => result.confirmed);
}

/** チェックの状態も一緒に受け取る確認。チェックを添えない呼び出しは `checked: false` が返る。 */
export function confirmActionWithChoice(options: ConfirmationOptions): Promise<ConfirmationResult> {
  if (pending !== null) {
    throw new Error('確認ダイアログは既に開いています。');
  }

  const returnFocusTo = activeFocusTarget();
  return new Promise<ConfirmationResult>((resolve) => {
    pending = {
      ...options,
      danger: options.danger ?? false,
      checkbox: options.checkbox === undefined ? undefined : { ...options.checkbox },
      id: ++nextId,
      resolve,
      returnFocusTo,
    };
  });
}

/** ダイアログのチェックを切り替える。 */
export function setConfirmationChecked(checked: boolean): void {
  if (pending?.checkbox !== undefined) {
    pending.checkbox.checked = checked;
  }
}

export function acceptConfirmation(): void {
  settle(true);
}

export function cancelConfirmation(): void {
  settle(false);
}

export function handleConfirmationKeydown(
  event: ConfirmationKeyEvent,
  cancelButton: FocusTarget | null,
  confirmButton: FocusTarget | null,
  activeElement: unknown,
): void {
  if (event.key === 'Escape') {
    event.preventDefault();
    cancelConfirmation();
    return;
  }

  if (event.key !== 'Tab' || cancelButton === null || confirmButton === null) {
    return;
  }

  if (event.shiftKey && activeElement === cancelButton) {
    event.preventDefault();
    confirmButton.focus();
  } else if (!event.shiftKey && activeElement === confirmButton) {
    event.preventDefault();
    cancelButton.focus();
  }
}

function settle(confirmed: boolean): void {
  const request = pending;
  if (request === null) {
    return;
  }

  pending = null;
  queueMicrotask(() => request.returnFocusTo?.focus());
  request.resolve({ confirmed, checked: confirmed && (request.checkbox?.checked ?? false) });
}

function activeFocusTarget(): FocusTarget | null {
  if (typeof document === 'undefined') {
    return null;
  }

  const target = document.activeElement;
  if (target === null) {
    return null;
  }

  const focus = Reflect.get(target, 'focus');
  return typeof focus === 'function' ? { focus: () => focus.call(target) } : null;
}
