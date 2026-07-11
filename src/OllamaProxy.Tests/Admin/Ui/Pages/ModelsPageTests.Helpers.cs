// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using AngleSharp.Dom;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using OllamaProxy.Admin.Catalog;
using OllamaProxy.Configuration;
using OllamaProxy.Core;
using OllamaProxy.Providers.Abstractions;
// The page type OllamaProxy.Admin.Ui.Pages.Models is aliased for symmetry with the other page-test harnesses and
// to keep the SUT reference unambiguous against the OllamaProxy.Core model types imported here.
using ModelsPage = OllamaProxy.Admin.Ui.Pages.Models;

namespace OllamaProxy.Tests.Admin.Ui.Pages;

public sealed partial class ModelsPageTests : BunitContext
{
	/// <summary>
	/// Renders <see cref="ModelsPage"/> with the given fake catalog service registered. The page reads its catalog
	/// synchronously in <c>OnInitialized()</c>, so the returned component has already resolved one of its rendered
	/// states (not-ready, empty, or populated) by the time this returns.
	/// </summary>
	/// <param name="service">The fake catalog service; defaults to one returning <see cref="LiveCatalog.NotReady"/>.</param>
	/// <returns>The rendered page together with the fake service driving it.</returns>
	private (IRenderedComponent<ModelsPage> Component, FakeAdminCatalogService Service) RenderModels(
		FakeAdminCatalogService? service = null)
	{
		service ??= new FakeAdminCatalogService();

		Services.AddSingleton<IAdminCatalogService>(service);

		IRenderedComponent<ModelsPage> component = Render<ModelsPage>();
		return (component, service);
	}

	/// <summary>
	/// Normalizes rendered text so tests can assert multi-line Razor markup exactly without depending on indentation.
	/// </summary>
	/// <param name="element">The rendered element whose text should be normalized.</param>
	/// <returns>The element text with runs of whitespace collapsed to single spaces.</returns>
	private static string VisibleText(IElement element) => string.Join(
		" ",
		element.TextContent.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

	/// <summary>
	/// Reads the visible status paragraph text for the not-ready or empty-catalog page states.
	/// </summary>
	/// <param name="component">The rendered <see cref="ModelsPage"/> page.</param>
	/// <returns>The normalized status text.</returns>
	private static string ModelsStatusText(IRenderedComponent<ModelsPage> component) =>
		VisibleText(component.Find(".models-status"));

	/// <summary>
	/// Gets the model summary rows rendered in the live-catalog table.
	/// </summary>
	/// <param name="component">The rendered <see cref="ModelsPage"/> page.</param>
	/// <returns>The rendered summary rows in document order.</returns>
	private static IReadOnlyList<IElement> ModelRows(IRenderedComponent<ModelsPage> component) =>
		component.FindAll("tr.models-row");

	/// <summary>
	/// Asserts the page-owned cells of one model summary row without duplicating <c>CapabilityChips</c> internals.
	/// </summary>
	/// <param name="row">The rendered model summary row.</param>
	/// <param name="expectedName">The expected client-facing model name.</param>
	/// <param name="expectedBackend">The expected backend name.</param>
	/// <param name="expectedUpstream">The expected upstream model identifier.</param>
	/// <param name="expectedContext">The expected formatted context-window text.</param>
	/// <param name="expectedReasoning">The expected reasoning-effort text.</param>
	private static void AssertModelRow(
		IElement row,
		string   expectedName,
		string   expectedBackend,
		string   expectedUpstream,
		string   expectedContext,
		string   expectedReasoning)
	{
		IHtmlCollection<IElement> cells = row.Children;
		Assert.Equal(expectedName, row.QuerySelector(".models-name")?.TextContent);
		Assert.Equal(expectedBackend, cells[2].TextContent);
		Assert.Equal(expectedUpstream, cells[3].TextContent);
		Assert.Equal(expectedContext, cells[5].TextContent);
		Assert.Equal(expectedReasoning, cells[6].TextContent);
	}

	/// <summary>
	/// Reads the definition-list fields from the currently rendered detail panel as term/value pairs in document order.
	/// </summary>
	/// <param name="component">The rendered <see cref="ModelsPage"/> page.</param>
	/// <returns>The detail fields visible to the operator.</returns>
	private static IReadOnlyList<(string Term, string Value)> DetailFields(IRenderedComponent<ModelsPage> component) =>
		component.FindAll(".models-field")
			.Select(static field =>
				(
					Term: field.QuerySelector("dt")?.TextContent ?? string.Empty,
					Value: VisibleText(field.QuerySelector("dd")!)))
			.ToList();

	/// <summary>
	/// Builds a minimal <see cref="RegisteredModel"/> for catalog rows, defaulting the fields the page's summary row
	/// does not exercise so a test only states the values it asserts on.
	/// </summary>
	/// <param name="name">The client-facing model name.</param>
	/// <param name="backendName">The backend that serves the model.</param>
	/// <param name="upstreamModel">The upstream model identifier; defaults to <paramref name="name"/>.</param>
	/// <param name="capabilities">The resolved capabilities; defaults to <see cref="ModelCapabilities.CompletionOnly"/>.</param>
	/// <param name="contextLength">The context window in tokens.</param>
	/// <param name="reasoningEffort">The pinned reasoning effort, or <see langword="null"/> when none is pinned.</param>
	/// <param name="metadata">Optional provider metadata for the detail panel.</param>
	/// <returns>The constructed model.</returns>
	private static RegisteredModel CreateModel(
		string                 name,
		string                 backendName     = "primary",
		string?                upstreamModel   = null,
		ModelCapabilities?     capabilities    = null,
		long                   contextLength   = 4096,
		ReasoningEffort?       reasoningEffort = null,
		ProviderModelMetadata? metadata        = null) => new(
		name,
		backendName,
		upstreamModel ?? name,
		capabilities ?? ModelCapabilities.CompletionOnly,
		contextLength,
		reasoningEffort,
		CreatedAtUtc: null,
		metadata);

	/// <summary>
	/// A configurable fake <see cref="IAdminCatalogService"/> that returns a fixed <see cref="LiveCatalog"/> and
	/// counts how often the page read it. Defaults to <see cref="LiveCatalog.NotReady"/>.
	/// </summary>
	private sealed class FakeAdminCatalogService : IAdminCatalogService
	{
		/// <summary>
		/// The catalog handed back from <see cref="GetLiveCatalog"/>. Defaults to the not-ready state.
		/// </summary>
		public LiveCatalog Catalog { get; init; } = LiveCatalog.NotReady;

		/// <summary>
		/// The number of times <see cref="GetLiveCatalog"/> was called, so a test can assert the page read the
		/// catalog exactly once during its initial render.
		/// </summary>
		public int GetLiveCatalogCallCount { get; private set; }

		/// <inheritdoc/>
		public LiveCatalog GetLiveCatalog()
		{
			GetLiveCatalogCallCount++;
			return Catalog;
		}
	}
}
