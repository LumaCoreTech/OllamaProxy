// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using AngleSharp.Dom;

using Bunit;

using OllamaProxy.Admin.Ui.Components;

namespace OllamaProxy.Tests.Admin.Ui.Components;

/// <summary>
/// Render and callback tests for <see cref="ConfirmDialog"/>, the reusable modal shown before destructive admin
/// actions. The component is a thin, declarative renderer: the owning page raises <see cref="ConfirmDialog.Visible"/>
/// to open it and supplies the copy, while the confirm and cancel buttons forward to
/// <see cref="ConfirmDialog.OnConfirm"/> and <see cref="ConfirmDialog.OnCancel"/>. These tests assert the rendered
/// content, the danger styling gate, and that each button raises exactly its own callback. The native open/close
/// behavior lives in confirmDialog.js and is out of scope here; JS interop is run in loose mode so the module
/// import in <c>OnAfterRenderAsync</c> is a no-op.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConfirmDialogTests : BunitContext
{
	/// <summary>
	/// Initializes the test context with loose JS interop so the component's <c>./confirmDialog.js</c> import and
	/// its <c>show</c> / <c>close</c> calls resolve without explicit setup — the DOM these tests assert on is
	/// rendered independently of JS.
	/// </summary>
	public ConfirmDialogTests()
	{
		JSInterop.Mode = JSRuntimeMode.Loose;
	}

	/// <summary>
	/// Verifies that the dialog renders its title, message, and both button labels, wiring the accessible name and
	/// description to the title and message via <c>aria-labelledby</c> / <c>aria-describedby</c>.
	/// </summary>
	[Fact]
	public void Render_WithContent_RendersTitleMessageAndLabels()
	{
		// Act
		IRenderedComponent<ConfirmDialog> cut = RenderDialog(
			title: "Remove backend?",
			message: "This cannot be undone.",
			confirmLabel: "Remove",
			cancelLabel: "Cancel");

		// Assert
		IElement dialog = cut.Find("dialog.confirm-dialog");
		IElement title = cut.Find("h2.confirm-dialog-title");
		IElement message = cut.Find("p.confirm-dialog-message");
		Assert.Equal("Remove backend?", title.TextContent.Trim());
		Assert.Equal("This cannot be undone.", message.TextContent.Trim());
		Assert.Equal("Remove", cut.Find("button.confirm-dialog-confirm").TextContent.Trim());
		Assert.Equal("Cancel", cut.Find("button.confirm-dialog-cancel").TextContent.Trim());

		// The dialog is linked to its own title and message for assistive technology.
		Assert.Equal(title.Id, dialog.GetAttribute("aria-labelledby"));
		Assert.Equal(message.Id, dialog.GetAttribute("aria-describedby"));
	}

	/// <summary>
	/// Verifies that a danger dialog tints the confirm button with the danger palette class so an irreversible
	/// choice is visually distinct from a routine confirmation.
	/// </summary>
	[Fact]
	public void Render_WhenDanger_AddsDangerConfirmClass()
	{
		// Act
		IRenderedComponent<ConfirmDialog> cut = RenderDialog(danger: true);

		// Assert
		IElement confirm = cut.Find("button.confirm-dialog-confirm");
		Assert.Contains("confirm-dialog-confirm-danger", confirm.ClassList);
		Assert.Contains("confirm-dialog-danger", cut.Find("dialog.confirm-dialog").ClassList);
	}

	/// <summary>
	/// Verifies that a non-danger dialog omits the danger palette classes, leaving the neutral confirm styling.
	/// </summary>
	[Fact]
	public void Render_WhenNotDanger_OmitsDangerClasses()
	{
		// Act
		IRenderedComponent<ConfirmDialog> cut = RenderDialog(danger: false);

		// Assert
		Assert.DoesNotContain("confirm-dialog-confirm-danger", cut.Find("button.confirm-dialog-confirm").ClassList);
		Assert.DoesNotContain("confirm-dialog-danger", cut.Find("dialog.confirm-dialog").ClassList);
	}

	/// <summary>
	/// Verifies that clicking the confirm button raises <see cref="ConfirmDialog.OnConfirm"/> exactly once and
	/// leaves <see cref="ConfirmDialog.OnCancel"/> untouched, so a confirmation is never mistaken for a dismissal.
	/// </summary>
	[Fact]
	public void OnConfirm_WhenConfirmClicked_InvokesOnConfirmOnly()
	{
		// Arrange
		int confirmCount = 0;
		int cancelCount = 0;
		IRenderedComponent<ConfirmDialog> cut = RenderDialog(
			onConfirm: () => confirmCount++,
			onCancel: () => cancelCount++);

		// Act
		cut.Find("button.confirm-dialog-confirm").Click();

		// Assert
		Assert.Equal(1, confirmCount);
		Assert.Equal(0, cancelCount);
	}

	/// <summary>
	/// Verifies that clicking the cancel button raises <see cref="ConfirmDialog.OnCancel"/> exactly once and leaves
	/// <see cref="ConfirmDialog.OnConfirm"/> untouched, so a dismissal never triggers the destructive action.
	/// </summary>
	[Fact]
	public void OnCancel_WhenCancelClicked_InvokesOnCancelOnly()
	{
		// Arrange
		int confirmCount = 0;
		int cancelCount = 0;
		IRenderedComponent<ConfirmDialog> cut = RenderDialog(
			onConfirm: () => confirmCount++,
			onCancel: () => cancelCount++);

		// Act
		cut.Find("button.confirm-dialog-cancel").Click();

		// Assert
		Assert.Equal(1, cancelCount);
		Assert.Equal(0, confirmCount);
	}

	/// <summary>
	/// Verifies that the native <c>cancel</c> bridge (<see cref="ConfirmDialog.OnNativeCancelAsync"/>) forwards to
	/// <see cref="ConfirmDialog.OnCancel"/>, so an Escape-key or backdrop dismissal notifies the owner just like the
	/// cancel button does.
	/// </summary>
	[Fact]
	public async Task OnNativeCancelAsync_WhenInvoked_InvokesOnCancel()
	{
		// Arrange
		int cancelCount = 0;
		IRenderedComponent<ConfirmDialog> cut = RenderDialog(visible: true, onCancel: () => cancelCount++);

		// Act: simulate the JS module forwarding the native cancel event.
		await cut.InvokeAsync(() => cut.Instance.OnNativeCancelAsync());

		// Assert
		Assert.Equal(1, cancelCount);
	}

	/// <summary>
	/// Verifies that after a native cancel (Escape / backdrop) the reconcile still issues the interop
	/// <c>close</c> once the owner lowers <see cref="ConfirmDialog.Visible"/>. Regression guard: the native
	/// <c>cancel</c> handler calls <c>preventDefault()</c>, so the dialog stays open and .NET must drive the close;
	/// if <see cref="ConfirmDialog.OnNativeCancelAsync"/> wrongly cleared its open-state tracking, the
	/// <c>Visible</c> transition would be seen as a no-op and the (now content-less) dialog would remain stuck open.
	/// </summary>
	/// <returns>A task that completes once the assertion has run.</returns>
	[Fact]
	public async Task OnNativeCancelAsync_ThenVisibleLowered_ClosesDialog()
	{
		// Arrange: capture the interop module so its show/close calls can be asserted, then open the dialog.
		BunitJSModuleInterop module = JSInterop.SetupModule("./confirmDialog.js");
		module.SetupVoid("show").SetVoidResult();
		module.SetupVoid("close").SetVoidResult();
		IRenderedComponent<ConfirmDialog> cut = RenderDialog(visible: true);

		// Act: the browser dismisses via Escape (native cancel), then the owner reacts by lowering Visible — the
		// exact sequence that previously left the dialog stuck open.
		await cut.InvokeAsync(() => cut.Instance.OnNativeCancelAsync());
		cut.Render(parameters => parameters
			.Add(component => component.Visible, false)
			.Add(component => component.Title, "Confirm?")
			.Add(component => component.Message, "Are you sure?"));

		// Assert: the reconcile issued exactly one close() on the dialog element after the native cancel. The
		// reconcile runs in OnAfterRenderAsync, whose interop await completes asynchronously, so wait for it.
		await cut.WaitForAssertionAsync(() => Assert.Single(
			module.Invocations,
			invocation => invocation.Identifier == "close"));
	}

	/// <summary>
	/// Renders <see cref="ConfirmDialog"/> with the supplied parameters, defaulting to a benign visible dialog so
	/// individual tests only override what they exercise.
	/// </summary>
	/// <param name="visible">Whether the dialog is shown.</param>
	/// <param name="title">The dialog heading.</param>
	/// <param name="message">The dialog body text.</param>
	/// <param name="confirmLabel">The confirm button label.</param>
	/// <param name="cancelLabel">The cancel button label.</param>
	/// <param name="danger">Whether the dialog uses the destructive-action palette.</param>
	/// <param name="onConfirm">The confirm callback, or <see langword="null"/> for a no-op.</param>
	/// <param name="onCancel">The cancel callback, or <see langword="null"/> for a no-op.</param>
	/// <returns>The rendered <see cref="ConfirmDialog"/> component.</returns>
	private IRenderedComponent<ConfirmDialog> RenderDialog(
		bool    visible      = true,
		string  title        = "Confirm?",
		string  message      = "Are you sure?",
		string  confirmLabel = "Confirm",
		string  cancelLabel  = "Cancel",
		bool    danger       = false,
		Action? onConfirm    = null,
		Action? onCancel     = null)
	{
		return Render<ConfirmDialog>(parameters => parameters
			.Add(component => component.Visible, visible)
			.Add(component => component.Title, title)
			.Add(component => component.Message, message)
			.Add(component => component.ConfirmLabel, confirmLabel)
			.Add(component => component.CancelLabel, cancelLabel)
			.Add(component => component.Danger, danger)
			.Add(component => component.OnConfirm, () => onConfirm?.Invoke())
			.Add(component => component.OnCancel, () => onCancel?.Invoke()));
	}
}
