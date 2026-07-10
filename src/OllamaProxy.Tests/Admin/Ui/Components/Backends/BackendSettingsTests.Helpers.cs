// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using AngleSharp.Dom;

using Bunit;

using OllamaProxy.Admin.Editing;
using OllamaProxy.Admin.Ui.Components.Backends;
using OllamaProxy.Configuration;
using OllamaProxy.Providers.Abstractions;

namespace OllamaProxy.Tests.Admin.Ui.Components.Backends;

public sealed partial class BackendSettingsTests
{
	/// <summary>
	/// The provider descriptors the picker renders, in the order the fixture publishes them: a metadata-poor
	/// family that prefills a fixed URL (<c>openai</c>), a self-hosted family with no fixed endpoint (<c>vllm</c>),
	/// and a capability-rich family that defaults to plug-and-play (<c>venice</c>). Tests assert the provider
	/// <c>&lt;option&gt;</c> list against these exact values, so the picker is pinned to whatever catalog the page
	/// supplies rather than to any concrete catalog implementation.
	/// </summary>
	private static readonly IReadOnlyList<ProviderDescriptor> SampleProviders =
	[
		new("openai", "OpenAI", OperatingMode.Explicit, "https://api.openai.com/v1"),
		new("vllm", "vLLM", OperatingMode.Explicit, string.Empty),
		new("venice", "Venice", OperatingMode.PlugAndPlay, "https://api.venice.ai/api/v1")
	];

	/// <summary>
	/// Renders <see cref="BackendSettings"/> with sensible defaults for every required parameter, so a test only
	/// supplies the inputs relevant to the branch it exercises. The component injects no services, so no
	/// container registration is needed.
	/// </summary>
	/// <param name="backend">The backend whose settings are rendered; defaults to an existing, Explicit backend.</param>
	/// <param name="providers">The provider catalog to render; defaults to <see cref="SampleProviders"/>.</param>
	/// <param name="isBusy">Whether the owning page is busy, mirrored to every interactive control.</param>
	/// <param name="configure">An optional hook to add event-callback parameters the test wants to observe.</param>
	/// <returns>The rendered <see cref="BackendSettings"/> component.</returns>
	private IRenderedComponent<BackendSettings> RenderSettings(
		DesiredBackend?                                               backend   = null,
		IReadOnlyList<ProviderDescriptor>?                            providers = null,
		bool                                                          isBusy    = false,
		Action<ComponentParameterCollectionBuilder<BackendSettings>>? configure = null)
	{
		backend ??= CreateBackend();
		providers ??= SampleProviders;

		return Render<BackendSettings>(parameters =>
		{
			parameters
				.Add(component => component.Backend, backend)
				.Add(component => component.Providers, providers)
				.Add(component => component.IsBusy, isBusy);

			configure?.Invoke(parameters);
		});
	}

	/// <summary>
	/// Builds a <see cref="DesiredBackend"/> fixture. Defaults describe an <em>existing</em> backend (its
	/// <see cref="DesiredBackend.OriginalName"/> is set) so the API-key field shows the saved-secret placeholder;
	/// pass <paramref name="originalName"/> as <see langword="null"/> to model a newly added backend.
	/// </summary>
	/// <param name="name">The current logical name shown in the Name field.</param>
	/// <param name="originalName">The name the backend loaded with, or <see langword="null"/> for a new backend.</param>
	/// <param name="providerType">The selected provider-type discriminator.</param>
	/// <param name="baseUrl">The base URL shown in the Base URL field.</param>
	/// <param name="apiKey">The API key value; blank models the write-only "keep saved key" state.</param>
	/// <param name="mode">The selected operating mode, or <see langword="null"/> for the provider-based default.</param>
	/// <returns>The assembled backend draft.</returns>
	private static DesiredBackend CreateBackend(
		string         name         = "openai-prod",
		string?        originalName = "openai-prod",
		string         providerType = "openai",
		string         baseUrl      = "https://api.openai.com/v1",
		string         apiKey       = "",
		OperatingMode? mode         = OperatingMode.Explicit) => new()
	{
		Name = name,
		OriginalName = originalName,
		Options = new BackendOptions
		{
			ProviderType = providerType,
			BaseUrl = baseUrl,
			ApiKey = apiKey,
			Mode = mode
		}
	};

	/// <summary>
	/// Gets the Provider <c>&lt;select&gt;</c>. The two selects carry no distinguishing class, so they are
	/// addressed by document order: Provider is authored before Mode in the component markup, so it is the first
	/// select. This coupling is intentional and localized here so the tests read by intent.
	/// </summary>
	/// <param name="cut">The rendered component.</param>
	/// <returns>The Provider select element.</returns>
	private static IElement ProviderSelect(IRenderedComponent<BackendSettings> cut) => cut.FindAll("select")[0];

	/// <summary>
	/// Gets the Mode <c>&lt;select&gt;</c>. See <see cref="ProviderSelect"/> for the document-order rationale;
	/// Mode is the last field in the markup, so it is the second select.
	/// </summary>
	/// <param name="cut">The rendered component.</param>
	/// <returns>The Mode select element.</returns>
	private static IElement ModeSelect(IRenderedComponent<BackendSettings> cut) => cut.FindAll("select")[1];

	/// <summary>
	/// Gets the visible label text of each field in document order, isolating the label's own text node from any
	/// nested control's text (the Provider and Mode labels wrap a <c>&lt;select&gt;</c> whose option text would
	/// otherwise leak into the label's <see cref="INode.TextContent"/>).
	/// </summary>
	/// <param name="cut">The rendered component.</param>
	/// <returns>The trimmed label texts, one per field, in render order.</returns>
	private static IReadOnlyList<string> FieldLabels(IRenderedComponent<BackendSettings> cut)
	{
		return cut
			.FindAll("div.backend-field label")
			.Select(label => label.FirstChild!.TextContent.Trim())
			.ToList();
	}

	/// <summary>
	/// Gets every interactive control in the form (the three inputs and two selects) in document order, so the
	/// busy-state tests can assert the disabled attribute across the whole editor at once.
	/// </summary>
	/// <param name="cut">The rendered component.</param>
	/// <returns>The interactive control elements.</returns>
	private static IReadOnlyList<IElement> InteractiveControls(IRenderedComponent<BackendSettings> cut)
	{
		return cut.FindAll("div.backend-field input, div.backend-field select");
	}

	/// <summary>
	/// Extracts the <c>(value, text)</c> pair of every <c>&lt;option&gt;</c> in a select, so option lists can be
	/// asserted as a single exact sequence.
	/// </summary>
	/// <param name="select">The select whose options are read.</param>
	/// <returns>The option value/text pairs in document order.</returns>
	private static IReadOnlyList<(string Value, string Text)> OptionPairs(IElement select)
	{
		return select
			.QuerySelectorAll("option")
			.Select(option => (option.GetAttribute("value") ?? string.Empty, option.TextContent.Trim()))
			.ToList();
	}
}
