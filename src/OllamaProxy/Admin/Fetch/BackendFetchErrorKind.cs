// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

namespace OllamaProxy.Admin.Fetch;

/// <summary>
/// Why an admin backend fetch failed. The failure is classified only as far as it can be attributed honestly.
/// The admin surface uses this to tell the operator where the fix lies: a credential (theirs), an upstream
/// outage (the backend's), or something the proxy could not place. It never guesses a cause it cannot prove.
/// </summary>
public enum BackendFetchErrorKind
{
	/// <summary>
	/// The backend rejected the proxy's credentials: an upstream HTTP <c>401 Unauthorized</c> or
	/// <c>403 Forbidden</c>. The operator fixes this by correcting the backend's API key or its permissions,
	/// not by retrying.
	/// </summary>
	Authentication,

	/// <summary>
	/// The backend was reached but answered with a non-authentication error status (any other upstream HTTP
	/// failure: a <c>404</c>, <c>429</c>, <c>5xx</c>, and so on). The fault lies with the backend or the route,
	/// so the operator's recourse is to check the backend's health or its base address.
	/// </summary>
	Upstream,

	/// <summary>
	/// The failure could not be attributed to the backend with confidence: a transport fault (DNS, connection
	/// refused, TLS), a malformed response, or any other non-provider exception. This is deliberately distinct
	/// from <see cref="Upstream"/>. Blaming the upstream server for a fault the proxy cannot prove originated
	/// there would mislead the operator, so an unattributable failure is reported honestly as unknown.
	/// </summary>
	Unknown
}
