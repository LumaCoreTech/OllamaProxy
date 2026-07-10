// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

// Interop for the ConfirmDialog component.
//
// Blazor cannot call the native <dialog> methods showModal() / close() declaratively, so this module owns the
// two calls. It also bridges the dialog's own dismissal paths — the Escape key and a backdrop click, which both
// fire the DOM 'cancel' event — back to .NET, so the owning page's OnCancel runs no matter how the dialog closed.
//
// The .NET reference and the listener are tracked per dialog element via a WeakMap, so several ConfirmDialogs on
// one page stay independent and a closed dialog leaks nothing.

const handlers = new WeakMap();

// show(dialog, dotNetRef) opens the dialog as a modal and wires the native 'cancel' event to the component's
// [JSInvokable] OnNativeCancelAsync. Calling show on an already-open dialog is a no-op (showModal throws on a
// dialog that is already open, so we guard against it).
export function show(dialog, dotNetRef) {
    if (!dialog || dialog.open) return;

    // preventDefault stops the browser's synchronous close so .NET drives the close through the Visible flag on
    // the next render — keeping the open/closed state single-sourced on the component side.
    const onCancel = (event) => {
        event.preventDefault();
        dotNetRef.invokeMethodAsync('OnNativeCancelAsync');
    };

    dialog.addEventListener('cancel', onCancel);
    handlers.set(dialog, onCancel);

    dialog.showModal();
}

// close(dialog) closes the dialog if open and detaches the 'cancel' listener registered by show(). Safe to call
// on an already-closed dialog.
export function close(dialog) {
    if (!dialog) return;

    const onCancel = handlers.get(dialog);
    if (onCancel) {
        dialog.removeEventListener('cancel', onCancel);
        handlers.delete(dialog);
    }

    if (dialog.open) {
        dialog.close();
    }
}
