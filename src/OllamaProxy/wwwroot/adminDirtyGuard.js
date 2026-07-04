// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

// Unsaved-changes guard for the admin Backends page.
//
// Blazor cannot subscribe to the browser's beforeunload event directly, so this module owns the listener and
// keeps a single boolean flag. The page calls setDirty(true) after every user edit and setDirty(false) after a
// successful apply or a fresh load. When the flag is set, the module asks the browser to confirm before the user
// closes the tab, refreshes, or navigates away from the SPA. The in-app navigation case is handled separately on
// the .NET side through NavigationManager.RegisterLocationChangingHandler, so this module only covers the
// browser-level events.
//
// Browsers ignore custom strings and render their own dialog ("Changes you made may not be saved"), so we return
// a non-empty value purely to satisfy the spec and let the browser do the talking. The flag is module-private,
// so no global state is leaked into the page.

const dirtyFlag = { value: false };
let attached = false;

function handleBeforeUnload(event) {
    if (!dirtyFlag.value) return undefined;

    // Cancel the unload and show the browser's own confirmation dialog. The return value is required by the spec;
    // modern browsers ignore its content and render their own message.
    event.preventDefault();
    event.returnValue = '';
    return '';
}

export function setDirty(isDirty) {
    dirtyFlag.value = Boolean(isDirty);
}

// attach(dotNetRef) wires up the beforeunload listener. dotNetRef is currently unused (the page pushes the
// dirty status directly via setDirty) but it is accepted so a future caller can let the module pull the status
// from the .NET side instead. The returned teardown function detaches the listener and clears the flag.
export function attach() {
    if (attached) return;
    window.addEventListener('beforeunload', handleBeforeUnload);
    attached = true;
}

export function detach() {
    if (!attached) return;
    window.removeEventListener('beforeunload', handleBeforeUnload);
    attached = false;
    dirtyFlag.value = false;
}
