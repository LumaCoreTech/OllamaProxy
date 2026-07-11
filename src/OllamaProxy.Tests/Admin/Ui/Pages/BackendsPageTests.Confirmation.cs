// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Bunit;

using OllamaProxy.Admin.Ui.Pages;

namespace OllamaProxy.Tests.Admin.Ui.Pages;

// Remove confirmation: the destructive remove-backend action routed through the shared confirm dialog.
//
// Removing a backend is destructive, so the page never deletes immediately: clicking a card's "Remove backend"
// opens the page-scoped ConfirmDialog and defers the actual removal to its confirm action. Confirming drops the
// backend from the draft (one fewer card); cancelling leaves the draft untouched (the card stays). The card must
// be expanded first, since the remove button lives inside the editor panel.
//
// For the load lifecycle and shared harness see the anchor file and Helpers.
public sealed partial class BackendsPageTests
{
	// --- 5. Remove confirmation: confirm drops the backend, cancel keeps it ---

	/// <summary>
	/// Verifies that confirming the remove prompt drops the backend from the draft, leaving one fewer card, so the
	/// destructive action takes effect only after the operator confirms.
	/// </summary>
	[Fact]
	public void RequestRemoveBackend_WhenConfirmed_RemovesTheCard()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(
				CreateBackend("primary"),
				CreateBackend("secondary"))
		};
		(IRenderedComponent<Backends> component, FakeAdminModelService _) = RenderBackends(service);

		// Expand the first card so its editor panel (and the Remove button) renders.
		component.FindAll("button.backend-card-header")[0].Click();

		// Act
		component.Find("button.backend-remove").Click();
		component.Find("button.confirm-dialog-confirm").Click();

		// Assert
		Assert.Equal(["secondary"], BackendCardNames(component));
		Assert.Single(component.FindAll(".backends-dirty-banner"));
	}

	/// <summary>
	/// Verifies that cancelling the remove prompt leaves the draft untouched, so both cards remain: the destructive
	/// action is genuinely gated on the confirmation rather than fired on the initial click.
	/// </summary>
	[Fact]
	public void RequestRemoveBackend_WhenCancelled_KeepsTheCard()
	{
		// Arrange
		FakeAdminModelService service = new()
		{
			StateFactory = static () => CreateDraft(
				CreateBackend("primary"),
				CreateBackend("secondary"))
		};
		(IRenderedComponent<Backends> component, FakeAdminModelService _) = RenderBackends(service);
		component.FindAll("button.backend-card-header")[0].Click();

		// Act
		component.Find("button.backend-remove").Click();
		component.Find("button.confirm-dialog-cancel").Click();

		// Assert
		Assert.Equal(["primary", "secondary"], BackendCardNames(component));
		Assert.Empty(component.FindAll(".backends-dirty-banner"));
	}
}
