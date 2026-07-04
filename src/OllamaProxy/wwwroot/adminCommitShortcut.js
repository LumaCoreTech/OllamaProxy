// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

// Commit-bar keyboard shortcut for the admin configuration pages.
//
// Once the operator has scrolled or tabbed deep into a long backend list there is no quick keyboard path back to
// the Apply/Discard bar. This module owns a single window-level keydown listener that watches for Ctrl+Enter and,
// on a match, asks the .NET side to move focus to the commit bar.
//
// Why a JS listener instead of a Blazor @onkeydown on the page container: this is Blazor Server, so a component
// handler would round-trip every keystroke in every field over SignalR and make typing in the many backend
// inputs feel laggy. Filtering here means the circuit is only invoked on the exact Ctrl+Enter chord, so ordinary
// typing never leaves the browser.

let attached = false;
let dotNetRef = null;

function handleKeyDown(event) {
    // Match the plain Ctrl+Enter chord only. Requiring Alt/Shift to be absent keeps the shortcut from firing on
    // richer combinations a browser or extension may own, and leaves a bare Enter (newline in a textarea) alone.
    // event.key covers both the main Enter and the numpad Enter; event.repeat skips auto-repeat while held.
    if (event.repeat) return;
    if (event.key !== 'Enter') return;
    if (!event.ctrlKey || event.altKey || event.shiftKey || event.metaKey) return;
    if (dotNetRef === null) return;

    // Claim the chord so the browser does not also treat it as a form submit or default activation.
    event.preventDefault();

    // Best-effort: if the circuit was torn down between the keypress and this dispatch the invoke rejects; there
    // is nothing to focus on a dead circuit, so the rejection is swallowed rather than surfaced to the operator.
    dotNetRef.invokeMethodAsync('JumpToCommitBarAsync').catch(() => {});
}

// attach(reference) wires up the listener and stores the .NET object reference the callback is invoked on. The
// page passes a DotNetObjectReference to itself; the module holds it only until detach() clears it. Calling
// attach twice is a no-op beyond refreshing the reference, so a re-render cannot stack duplicate listeners.
export function attach(reference) {
    dotNetRef = reference;
    if (attached) return;
    window.addEventListener('keydown', handleKeyDown);
    attached = true;
}

export function detach() {
    if (attached) {
        window.removeEventListener('keydown', handleKeyDown);
        attached = false;
    }

    // Drop the reference regardless of listener state so the module never holds a stale .NET handle past teardown.
    dotNetRef = null;
}
