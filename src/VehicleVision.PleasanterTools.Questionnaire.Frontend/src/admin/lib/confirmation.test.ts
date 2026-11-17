import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  acceptConfirmation,
  cancelConfirmation,
  confirmAction,
  confirmActionWithChoice,
  currentConfirmation,
  setConfirmationChecked,
  handleConfirmationKeydown,
} from './confirmation.svelte';

const options = {
  title: '終了する',
  message: 'この端末のセッションを終了します。よろしいですか？',
  confirmLabel: '終了する',
  danger: true,
};

afterEach(() => {
  cancelConfirmation();
  vi.unstubAllGlobals();
});

describe('確認ダイアログ', () => {
  it('呼び出すと指定内容で開く', () => {
    void confirmAction(options);

    expect(currentConfirmation()).toMatchObject(options);
  });

  it('取消で false を返して閉じる', async () => {
    const result = confirmAction(options);

    cancelConfirmation();

    await expect(result).resolves.toBe(false);
    expect(currentConfirmation()).toBeNull();
  });

  it('決定で true を返して閉じる', async () => {
    const result = confirmAction(options);

    acceptConfirmation();

    await expect(result).resolves.toBe(true);
    expect(currentConfirmation()).toBeNull();
  });

  it('Esc と同じ取消処理で false を返す', async () => {
    const result = confirmAction(options);
    const preventDefault = vi.fn();

    handleConfirmationKeydown(
      { key: 'Escape', shiftKey: false, preventDefault },
      null,
      null,
      null,
    );

    await expect(result).resolves.toBe(false);
    expect(preventDefault).toHaveBeenCalledOnce();
  });

  it('Tab の移動をダイアログ内のボタンに閉じ込める', () => {
    const cancel = { focus: vi.fn() };
    const confirm = { focus: vi.fn() };
    const preventDefault = vi.fn();

    handleConfirmationKeydown(
      { key: 'Tab', shiftKey: false, preventDefault },
      cancel,
      confirm,
      confirm,
    );

    expect(preventDefault).toHaveBeenCalledOnce();
    expect(cancel.focus).toHaveBeenCalledOnce();
  });

  it('閉じると開く前に焦点があった要素へ戻す', async () => {
    const focus = vi.fn();
    vi.stubGlobal('document', { activeElement: { focus } });
    const result = confirmAction(options);

    cancelConfirmation();
    await result;
    await new Promise<void>((resolve) => queueMicrotask(resolve));

    expect(focus).toHaveBeenCalledOnce();
  });
});

describe('チェックつきの確認（API キーも失効させる。Issue #1025）', () => {
  const withCheckbox = {
    ...options,
    checkbox: { label: 'この人の API キーも失効させる', checked: true },
  };

  it('初期値は呼び出し側が決め、そのまま決定すると true が返る', async () => {
    const result = confirmActionWithChoice(withCheckbox);

    expect(currentConfirmation()?.checkbox).toEqual({
      label: 'この人の API キーも失効させる',
      checked: true,
    });
    acceptConfirmation();

    await expect(result).resolves.toEqual({ confirmed: true, checked: true });
  });

  it('チェックを外して決定すると、外した状態が返る', async () => {
    const result = confirmActionWithChoice(withCheckbox);

    setConfirmationChecked(false);
    acceptConfirmation();

    await expect(result).resolves.toEqual({ confirmed: true, checked: false });
  });

  it('取消なら、チェックが入っていても実行しない（checked は false）', async () => {
    const result = confirmActionWithChoice(withCheckbox);

    cancelConfirmation();

    await expect(result).resolves.toEqual({ confirmed: false, checked: false });
  });

  it('呼び出し側が渡したオブジェクトを書き換えない', async () => {
    const checkbox = { label: 'x', checked: true };
    const result = confirmActionWithChoice({ ...options, checkbox });

    setConfirmationChecked(false);
    acceptConfirmation();
    await result;

    expect(checkbox.checked).toBe(true);
  });

  it('チェックを添えない確認では、チェックの切り替えは何もしない', async () => {
    const result = confirmAction(options);

    setConfirmationChecked(true);
    acceptConfirmation();

    await expect(result).resolves.toBe(true);
  });
});
