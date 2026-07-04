// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.AspNetCore.Components;

namespace OllamaProxy.Admin.Ui.Components;

/// <summary>
/// Inline SVG logo for OllamaProxy with an animated blinking eye.
/// </summary>
public partial class OllamaProxyLogo : ComponentBase
{
	/// <summary>
	/// CSS class applied to the SVG root.
	/// </summary>
	[Parameter]
	public string? Class { get; set; }

	/// <summary>
	/// Additional attributes forwarded to the SVG root element.
	/// </summary>
	[Parameter(CaptureUnmatchedValues = true)]
	public Dictionary<string, object?>? AdditionalAttributes { get; set; }

	/// <summary>
	/// Whether the logo should periodically blink its eye.
	/// </summary>
	[Parameter]
	public bool ShouldBlink { get; set; }
}
