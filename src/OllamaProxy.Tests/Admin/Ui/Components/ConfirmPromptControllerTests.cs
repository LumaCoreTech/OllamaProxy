// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using OllamaProxy.Admin.Ui.Components;

namespace OllamaProxy.Tests.Admin.Ui.Components;

/// <summary>
/// Behavioral tests for <see cref="ConfirmPromptController"/>, the page-agnostic bridge between the declarative
/// <see cref="ConfirmDialog"/> and both confirmation styles: fire-and-forget prompts via
/// <see cref="ConfirmPromptController.Request"/> and await-for-decision gates via
/// <see cref="ConfirmPromptController.RequestAsync"/>.
/// 
/// The tests follow the lifecycle of a single prompt slot, from opening through resolution to teardown:
/// 
/// 1. Empty state: no prompt shown, the exposed dialog properties fall back to their defaults
/// (InitialState_*).
/// 
/// 2. Request(): a fire-and-forget prompt opens, exposes its copy, and its own confirm/cancel actions run on the
/// matching resolution (Request_*, Confirm_*, Cancel_*).
/// 
/// 3. RequestAsync(): the returned task completes with the operator's decision — true on confirm, false on cancel
/// or on teardown via CancelPending (RequestAsync_*, CancelPending_*).
/// 
/// 4. Re-render notification: opening and resolving a prompt invoke the onChanged callback so the page re-renders;
/// CancelPending intentionally does not (OnChanged_*, CancelPending_WhenPending_DoesNotInvokeOnChanged).
/// 
/// 5. Argument guards: Request rejects a null prompt; RequestAsync rejects null copy (Request_WhenPromptIsNull_*,
/// RequestAsync_When*IsNull_*).
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConfirmPromptControllerTests
{
	// --- 1. Initial (empty) state ---

	/// <summary>
	/// Verifies that a freshly constructed controller shows no prompt and exposes the default dialog copy, so the
	/// bound <see cref="ConfirmDialog"/> stays hidden with benign labels until the first prompt opens.
	/// </summary>
	[Fact]
	public void InitialState_WhenNoPrompt_ExposesHiddenDefaults()
	{
		// Arrange
		ConfirmPromptController sut = new();

		// Act + Assert
		Assert.False(sut.IsVisible);
		Assert.Equal(string.Empty, sut.Title);
		Assert.Equal(string.Empty, sut.Message);
		Assert.Equal("Confirm", sut.ConfirmLabel);
		Assert.Equal("Cancel", sut.CancelLabel);
		Assert.False(sut.Danger);
	}

	// --- 2. Request(): fire-and-forget prompts ---

	/// <summary>
	/// Verifies that <see cref="ConfirmPromptController.Request"/> opens the prompt and surfaces its full copy
	/// through the dialog-binding properties, so the shared dialog renders exactly what the caller supplied.
	/// </summary>
	[Fact]
	public void Request_WithPrompt_ExposesPromptCopy()
	{
		// Arrange
		ConfirmPromptController sut = new();
		ConfirmPrompt prompt = new()
		{
			Title = "Remove backend?",
			Message = "Remove \"ollama-local\" from the draft?",
			ConfirmLabel = "Remove",
			CancelLabel = "Keep",
			Danger = true,
			Confirm = static () => { },
			Cancel = static () => { }
		};

		// Act
		sut.Request(prompt);

		// Assert
		Assert.True(sut.IsVisible);
		Assert.Equal("Remove backend?", sut.Title);
		Assert.Equal("Remove \"ollama-local\" from the draft?", sut.Message);
		Assert.Equal("Remove", sut.ConfirmLabel);
		Assert.Equal("Keep", sut.CancelLabel);
		Assert.True(sut.Danger);
	}

	/// <summary>
	/// Verifies that <see cref="ConfirmPromptController.Confirm"/> runs the active prompt's
	/// <see cref="ConfirmPrompt.Confirm"/> action, closes the dialog, and leaves the prompt's
	/// <see cref="ConfirmPrompt.Cancel"/> action untouched, so a confirmation is never mistaken for a dismissal.
	/// </summary>
	[Fact]
	public void Confirm_WhenPrompt_RunsConfirmActionAndClears()
	{
		// Arrange
		ConfirmPromptController sut = new();
		int confirmCount = 0;
		int cancelCount = 0;
		sut.Request(NewPrompt(onConfirm: () => confirmCount++, onCancel: () => cancelCount++));

		// Act
		sut.Confirm();

		// Assert
		Assert.Equal(1, confirmCount);
		Assert.Equal(0, cancelCount);
		Assert.False(sut.IsVisible);
	}

	/// <summary>
	/// Verifies that <see cref="ConfirmPromptController.Cancel"/> runs the active prompt's
	/// <see cref="ConfirmPrompt.Cancel"/> action, closes the dialog, and leaves the prompt's
	/// <see cref="ConfirmPrompt.Confirm"/> action untouched, so a dismissal never triggers the destructive action.
	/// </summary>
	[Fact]
	public void Cancel_WhenPrompt_RunsCancelActionAndClears()
	{
		// Arrange
		ConfirmPromptController sut = new();
		int confirmCount = 0;
		int cancelCount = 0;
		sut.Request(NewPrompt(onConfirm: () => confirmCount++, onCancel: () => cancelCount++));

		// Act
		sut.Cancel();

		// Assert
		Assert.Equal(0, confirmCount);
		Assert.Equal(1, cancelCount);
		Assert.False(sut.IsVisible);
	}

	/// <summary>
	/// Verifies that <see cref="ConfirmPromptController.Confirm"/> is a no-op when no prompt is open, so a stale
	/// confirmation arriving after the dialog was already dismissed does nothing.
	/// </summary>
	[Fact]
	public void Confirm_WhenNoPrompt_DoesNothing()
	{
		// Arrange
		ConfirmPromptController sut = new();

		// Act
		sut.Confirm();

		// Assert
		Assert.False(sut.IsVisible);
	}

	/// <summary>
	/// Verifies that <see cref="ConfirmPromptController.Cancel"/> is a no-op when no prompt is open, mirroring the
	/// stale-confirmation guard on <see cref="ConfirmPromptController.Confirm"/>.
	/// </summary>
	[Fact]
	public void Cancel_WhenNoPrompt_DoesNothing()
	{
		// Arrange
		ConfirmPromptController sut = new();

		// Act
		sut.Cancel();

		// Assert
		Assert.False(sut.IsVisible);
	}

	/// <summary>
	/// Verifies that a reentrant confirm action opening a new prompt survives, because the controller clears the
	/// active slot before running the action rather than after, so the freshly requested prompt is not overwritten.
	/// </summary>
	[Fact]
	public void Confirm_WhenActionRequestsNewPrompt_KeepsNewPrompt()
	{
		// Arrange
		ConfirmPromptController sut = new();
		ConfirmPrompt followUp = NewPrompt(title: "Follow-up?");

		// The first prompt's confirm action opens a second prompt — the classic "are you sure? … really sure?" chain.
		sut.Request(NewPrompt(onConfirm: () => sut.Request(followUp)));

		// Act
		sut.Confirm();

		// Assert
		Assert.True(sut.IsVisible);
		Assert.Equal("Follow-up?", sut.Title);
	}

	// --- 3. RequestAsync(): await-for-decision gates ---

	/// <summary>
	/// Verifies that the task returned by <see cref="ConfirmPromptController.RequestAsync"/> completes with
	/// <see langword="true"/> when the operator confirms, so a navigation gate proceeds only on an explicit yes.
	/// </summary>
	/// <returns>A task that completes when the assertion has run.</returns>
	[Fact]
	public async Task RequestAsync_WhenConfirmed_CompletesWithTrue()
	{
		// Arrange
		ConfirmPromptController sut = new();
		Task<bool> decision = sut.RequestAsync("Leave?", "Discard unsaved changes?", "Leave", danger: true);

		// Act
		sut.Confirm();
		bool result = await decision;

		// Assert
		Assert.True(result);
		Assert.False(sut.IsVisible);
	}

	/// <summary>
	/// Verifies that the task returned by <see cref="ConfirmPromptController.RequestAsync"/> completes with
	/// <see langword="false"/> when the operator cancels, so a navigation gate blocks the departure.
	/// </summary>
	/// <returns>A task that completes when the assertion has run.</returns>
	[Fact]
	public async Task RequestAsync_WhenCancelled_CompletesWithFalse()
	{
		// Arrange
		ConfirmPromptController sut = new();
		Task<bool> decision = sut.RequestAsync("Leave?", "Discard unsaved changes?", "Leave", danger: true);

		// Act
		sut.Cancel();
		bool result = await decision;

		// Assert
		Assert.False(result);
		Assert.False(sut.IsVisible);
	}

	/// <summary>
	/// Verifies that <see cref="ConfirmPromptController.RequestAsync"/> exposes the supplied copy through the
	/// dialog-binding properties, using the documented defaults for the optional cancel label.
	/// </summary>
	[Fact]
	public void RequestAsync_WithCopy_ExposesPromptCopy()
	{
		// Arrange
		ConfirmPromptController sut = new();

		// Act
		_ = sut.RequestAsync("Leave?", "Discard unsaved changes?", "Leave", danger: true);

		// Assert
		Assert.True(sut.IsVisible);
		Assert.Equal("Leave?", sut.Title);
		Assert.Equal("Discard unsaved changes?", sut.Message);
		Assert.Equal("Leave", sut.ConfirmLabel);
		Assert.Equal("Cancel", sut.CancelLabel);
		Assert.True(sut.Danger);
	}

	/// <summary>
	/// Verifies that <see cref="ConfirmPromptController.CancelPending"/> resolves a pending
	/// <see cref="ConfirmPromptController.RequestAsync"/> task with <see langword="false"/> ("stay"), so a
	/// navigation guard still awaiting the operator's decision unblocks when the circuit is torn down.
	/// </summary>
	/// <returns>A task that completes when the assertion has run.</returns>
	[Fact]
	public async Task CancelPending_WhenPending_CompletesDecisionWithFalse()
	{
		// Arrange
		ConfirmPromptController sut = new();
		Task<bool> decision = sut.RequestAsync("Leave?", "Discard unsaved changes?", "Leave", danger: true);

		// Act
		sut.CancelPending();
		bool result = await decision;

		// Assert
		Assert.False(result);
		Assert.False(sut.IsVisible);
	}

	// --- 4. Re-render notification ---

	/// <summary>
	/// Verifies that opening a prompt and resolving it each invoke the onChanged callback, so the owning page
	/// re-renders the dialog on both the open and the close transition.
	/// </summary>
	[Fact]
	public void OnChanged_OnRequestAndConfirm_InvokedForEachTransition()
	{
		// Arrange
		int changedCount = 0;
		ConfirmPromptController sut = new(() => changedCount++);

		// Act
		sut.Request(NewPrompt());
		sut.Confirm();

		// Assert
		Assert.Equal(2, changedCount);
	}

	/// <summary>
	/// Verifies that <see cref="ConfirmPromptController.CancelPending"/> does not invoke the onChanged callback,
	/// because it runs during teardown when the component is already going away and only the awaiting gate needs
	/// releasing — a re-render would touch a disposed component.
	/// </summary>
	[Fact]
	public void CancelPending_WhenPending_DoesNotInvokeOnChanged()
	{
		// Arrange
		int changedCount = 0;
		// ReSharper disable once AccessToModifiedClosure
		ConfirmPromptController sut = new(() => changedCount++);
		_ = sut.RequestAsync("Leave?", "Discard unsaved changes?");

		// The request itself notified once; clear so the assertion isolates CancelPending's behavior.
		changedCount = 0;

		// Act
		sut.CancelPending();

		// Assert
		Assert.Equal(0, changedCount);
	}

	// --- 5. Argument guards ---

	/// <summary>
	/// Verifies that <see cref="ConfirmPromptController.Request"/> rejects a <see langword="null"/> prompt, since a
	/// prompt with no copy or actions could never be rendered or resolved.
	/// </summary>
	[Fact]
	public void Request_WhenPromptIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		ConfirmPromptController sut = new();

		// Act + Assert
		var ex = Assert.Throws<ArgumentNullException>(() => sut.Request(null!));
		Assert.Equal("prompt", ex.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="ConfirmPromptController.RequestAsync"/> rejects a <see langword="null"/> title, so
	/// the dialog never opens without a heading.
	/// </summary>
	[Fact]
	public void RequestAsync_WhenTitleIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		ConfirmPromptController sut = new();

		// Act + Assert
		// The guard throws synchronously before any await, so this is not an async assertion despite the Task return.
		var ex = Assert.Throws<ArgumentNullException>(() => { _ = sut.RequestAsync(null!, "Message"); });
		Assert.Equal("title", ex.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="ConfirmPromptController.RequestAsync"/> rejects a <see langword="null"/> message, so
	/// the dialog never opens without body text.
	/// </summary>
	[Fact]
	public void RequestAsync_WhenMessageIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		ConfirmPromptController sut = new();

		// Act + Assert
		// The guard throws synchronously before any await, so this is not an async assertion despite the Task return.
		var ex = Assert.Throws<ArgumentNullException>(() => { _ = sut.RequestAsync("Title", null!); });
		Assert.Equal("message", ex.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="ConfirmPromptController.RequestAsync"/> rejects a <see langword="null"/> confirm
	/// label, so the confirm button always has a caption.
	/// </summary>
	[Fact]
	public void RequestAsync_WhenConfirmLabelIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		ConfirmPromptController sut = new();

		// Act + Assert
		// The guard throws synchronously before any await, so this is not an async assertion despite the Task return.
		var ex = Assert.Throws<ArgumentNullException>(() => { _ = sut.RequestAsync("Title", "Message", null!); });
		Assert.Equal("confirmLabel", ex.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="ConfirmPromptController.RequestAsync"/> rejects a <see langword="null"/> cancel
	/// label, so the cancel button always has a caption.
	/// </summary>
	[Fact]
	public void RequestAsync_WhenCancelLabelIsNull_ThrowsArgumentNullException()
	{
		// Arrange
		ConfirmPromptController sut = new();

		// Act + Assert
		// The guard throws synchronously before any await, so this is not an async assertion despite the Task return.
		var ex = Assert.Throws<ArgumentNullException>(() =>
		{
			_ = sut.RequestAsync("Title", "Message", "Confirm", null!);
		});
		Assert.Equal("cancelLabel", ex.ParamName);
	}

	/// <summary>
	/// Creates a benign <see cref="ConfirmPrompt"/> for tests that only care about a subset of its fields, letting
	/// each caller override the title and the confirm/cancel actions while defaulting the rest.
	/// </summary>
	/// <param name="title">The prompt heading.</param>
	/// <param name="onConfirm">The action to run on confirm, or a no-op when omitted.</param>
	/// <param name="onCancel">The action to run on cancel, or a no-op when omitted.</param>
	/// <returns>A fully populated prompt.</returns>
	private static ConfirmPrompt NewPrompt(
		string  title     = "Confirm?",
		Action? onConfirm = null,
		Action? onCancel  = null) => new()
	{
		Title = title,
		Message = "Are you sure?",
		Confirm = onConfirm ?? (static () => { }),
		Cancel = onCancel ?? (static () => { })
	};
}
