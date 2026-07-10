// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Admin.Ui.Components;

/// <summary>
/// A confirmation prompt shown in a page's single <see cref="ConfirmDialog"/>. It bundles the display copy
/// (title, message, button labels, danger styling) with the two actions that run when the operator answers, so
/// every destructive action reuses one dialog by supplying a different prompt rather than adding a new dialog
/// instance and visibility flag.
/// </summary>
public sealed record ConfirmPrompt
{
	/// <summary>
	/// The dialog heading, for example <c>Remove backend?</c>.
	/// </summary>
	public required string Title { get; init; }

	/// <summary>
	/// The explanatory body text describing the consequence of confirming.
	/// </summary>
	public required string Message { get; init; }

	/// <summary>
	/// The confirm button label. Defaults to <c>Confirm</c>; destructive actions override it with a specific verb
	/// such as <c>Remove</c> or <c>Leave</c>.
	/// </summary>
	public string ConfirmLabel { get; init; } = "Confirm";

	/// <summary>
	/// The cancel button label. Defaults to <c>Cancel</c>.
	/// </summary>
	public string CancelLabel { get; init; } = "Cancel";

	/// <summary>
	/// Whether the prompt confirms a destructive action, which switches the confirm button to the danger palette.
	/// </summary>
	public bool Danger { get; init; }

	/// <summary>
	/// The action run when the operator confirms.
	/// </summary>
	public required Action Confirm { get; init; }

	/// <summary>
	/// The action run when the operator dismisses the dialog by any path (cancel button, Escape, backdrop).
	/// </summary>
	public required Action Cancel { get; init; }
}

/// <summary>
/// Owns the single active confirmation prompt for a page and bridges the declarative <see cref="ConfirmDialog"/>
/// to both fire-and-forget confirmations (a destructive button that acts on confirm) and await-for-decision gates
/// (a navigation guard that must block until the operator answers). A modal is mutually exclusive, so a single
/// active-prompt slot both models the "only one question at a time" reality and structurally rules out two dialogs
/// being open at once.
/// </summary>
/// <remarks>
///     <para>
///     The controller is deliberately page-agnostic: it holds no Blazor component reference and instead takes a
///     re-render callback (typically <see cref="Microsoft.AspNetCore.Components.ComponentBase.StateHasChanged"/>) so it
///     can be unit-tested as a plain object. A page wires one instance up in its markup:
///     </para>
///     <code>
/// &lt;ConfirmDialog Visible="@Prompts.IsVisible" Title="@Prompts.Title" ...
///                OnConfirm="@Prompts.Confirm" OnCancel="@Prompts.Cancel"/&gt;
/// </code>
///     <para>
///     A destructive button calls <see cref="Request"/>; a navigation guard awaits <see cref="RequestAsync"/>. On
///     teardown the page calls <see cref="CancelPending"/> so a guard still awaiting the operator's decision unblocks
///     (resolving to "stay") instead of hanging past the circuit.
///     </para>
/// </remarks>
public sealed class ConfirmPromptController
{
	private readonly Action? mOnChanged;

	private ConfirmPrompt? mActive;

	/// <summary>
	/// Initializes a new controller.
	/// </summary>
	/// <param name="onChanged">
	/// A callback invoked whenever the active prompt opens or closes, so the owning page re-renders the dialog.
	/// Typically the component's <see cref="Microsoft.AspNetCore.Components.ComponentBase.StateHasChanged"/>. May be
	/// <see langword="null"/> in tests that assert state directly without a render loop.
	/// </param>
	public ConfirmPromptController(Action? onChanged = null)
	{
		mOnChanged = onChanged;
	}

	/// <summary>
	/// Gets whether a prompt is currently shown, which drives <see cref="ConfirmDialog.Visible"/>.
	/// </summary>
	public bool IsVisible => mActive is not null;

	/// <summary>
	/// Gets the active prompt's heading, or an empty string when no prompt is shown.
	/// </summary>
	public string Title => mActive?.Title ?? string.Empty;

	/// <summary>
	/// Gets the active prompt's body text, or an empty string when no prompt is shown.
	/// </summary>
	public string Message => mActive?.Message ?? string.Empty;

	/// <summary>
	/// Gets the active prompt's confirm button label, or <c>Confirm</c> when no prompt is shown.
	/// </summary>
	public string ConfirmLabel => mActive?.ConfirmLabel ?? "Confirm";

	/// <summary>
	/// Gets the active prompt's cancel button label, or <c>Cancel</c> when no prompt is shown.
	/// </summary>
	public string CancelLabel => mActive?.CancelLabel ?? "Cancel";

	/// <summary>
	/// Gets whether the active prompt uses the danger palette, or <see langword="false"/> when no prompt is shown.
	/// </summary>
	public bool Danger => mActive?.Danger ?? false;

	/// <summary>
	/// Opens a fire-and-forget prompt: the operator's answer runs the prompt's own <see cref="ConfirmPrompt.Confirm"/>
	/// or <see cref="ConfirmPrompt.Cancel"/> action and nothing is awaited. Suits a destructive button whose action
	/// happens on confirm (for example removing a backend). Replaces any prompt already open.
	/// </summary>
	/// <param name="prompt">The prompt to show.</param>
	/// <exception cref="ArgumentNullException"><paramref name="prompt"/> is <see langword="null"/>.</exception>
	public void Request(ConfirmPrompt prompt)
	{
		ArgumentNullException.ThrowIfNull(prompt);

		mActive = prompt;
		mOnChanged?.Invoke();
	}

	/// <summary>
	/// Opens a prompt and returns a task that completes with the operator's decision, bridging the declarative
	/// dialog to an imperative await-for-decision gate (for example a navigation guard that must block until the
	/// operator answers). Replaces any prompt already open.
	/// </summary>
	/// <param name="title">The dialog heading.</param>
	/// <param name="message">The explanatory body text.</param>
	/// <param name="confirmLabel">The confirm button label.</param>
	/// <param name="cancelLabel">The cancel button label.</param>
	/// <param name="danger">Whether the prompt uses the danger palette.</param>
	/// <returns>
	/// A task that completes with <see langword="true"/> when the operator confirms and <see langword="false"/>
	/// when they cancel (via the cancel button, Escape, backdrop, or <see cref="CancelPending"/> on teardown).
	/// </returns>
	/// <exception cref="ArgumentNullException">
	/// <paramref name="title"/>, <paramref name="message"/>, <paramref name="confirmLabel"/>, or
	/// <paramref name="cancelLabel"/> is <see langword="null"/>.
	/// </exception>
	public Task<bool> RequestAsync(
		string title,
		string message,
		string confirmLabel = "Confirm",
		string cancelLabel  = "Cancel",
		bool   danger       = false)
	{
		ArgumentNullException.ThrowIfNull(title);
		ArgumentNullException.ThrowIfNull(message);
		ArgumentNullException.ThrowIfNull(confirmLabel);
		ArgumentNullException.ThrowIfNull(cancelLabel);

		// RunContinuationsAsynchronously keeps the awaiting gate's continuation off the button-click call stack, so
		// resolving the prompt returns to the render loop cleanly rather than re-entering it inline.
		TaskCompletionSource<bool> decision = new(TaskCreationOptions.RunContinuationsAsynchronously);

		mActive = new ConfirmPrompt
		{
			Title = title,
			Message = message,
			ConfirmLabel = confirmLabel,
			CancelLabel = cancelLabel,
			Danger = danger,
			Confirm = () => decision.TrySetResult(true),
			Cancel = () => decision.TrySetResult(false)
		};
		mOnChanged?.Invoke();

		return decision.Task;
	}

	/// <summary>
	/// Resolves the active prompt as confirmed: closes the dialog and runs the prompt's
	/// <see cref="ConfirmPrompt.Confirm"/> action. A no-op when no prompt is active (a stale confirmation after the
	/// dialog was already dismissed). Bind this to <see cref="ConfirmDialog.OnConfirm"/>.
	/// </summary>
	public void Confirm()
	{
		ConfirmPrompt? prompt = mActive;
		if (prompt is null) return;

		// Clear before running so a reentrant action that opens a new prompt is not immediately overwritten.
		mActive = null;
		mOnChanged?.Invoke();
		prompt.Confirm();
	}

	/// <summary>
	/// Resolves the active prompt as cancelled: closes the dialog and runs the prompt's
	/// <see cref="ConfirmPrompt.Cancel"/> action. A no-op when no prompt is active. Bind this to
	/// <see cref="ConfirmDialog.OnCancel"/>.
	/// </summary>
	public void Cancel()
	{
		ConfirmPrompt? prompt = mActive;
		if (prompt is null) return;

		// Clear before running so a reentrant action that opens a new prompt is not immediately overwritten.
		mActive = null;
		mOnChanged?.Invoke();
		prompt.Cancel();
	}

	/// <summary>
	/// Resolves any open prompt as cancelled without notifying the render loop, for use during component disposal.
	/// A pending <see cref="RequestAsync"/> task completes with <see langword="false"/> ("stay"), so a navigation
	/// guard still awaiting the operator's decision unblocks instead of hanging past the torn-down circuit. A no-op
	/// when no prompt is active.
	/// </summary>
	public void CancelPending()
	{
		ConfirmPrompt? prompt = mActive;
		if (prompt is null) return;

		// No re-render on teardown: the component is going away, so only the awaiting gate needs to be released.
		mActive = null;
		prompt.Cancel();
	}
}
