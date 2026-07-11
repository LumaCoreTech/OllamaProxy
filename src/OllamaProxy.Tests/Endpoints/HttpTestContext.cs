// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace OllamaProxy.Tests.Endpoints;

/// <summary>
/// Test helpers for exercising the static HTTP response helpers on <c>OllamaHttp</c> and <c>OpenAiHttp</c>
/// against an in-memory <see cref="DefaultHttpContext"/>. The helpers build a context whose response body is a
/// caller-supplied stream, and one whose response feature reports <see cref="IHttpResponseFeature.HasStarted"/>
/// so the write helpers' already-started skip can be exercised.
/// </summary>
static class HttpTestContext
{
	/// <summary>
	/// Builds a context whose response writes into <paramref name="body"/>, for asserting the bytes a helper
	/// emits.
	/// </summary>
	/// <param name="body">The stream the response body writes into.</param>
	/// <returns>A context wired to capture the response body.</returns>
	public static DefaultHttpContext Create(Stream body)
	{
		DefaultHttpContext context = new();
		context.Response.Body = body;

		return context;
	}

	/// <summary>
	/// Builds a context whose response reports <see cref="IHttpResponseFeature.HasStarted"/> as
	/// <see langword="true"/>, so a helper's already-started guard can be exercised without committing a real
	/// response.
	/// </summary>
	/// <param name="body">The stream the response body writes into.</param>
	/// <returns>A context whose response feature reports the headers as already sent.</returns>
	public static DefaultHttpContext CreateStarted(Stream body)
	{
		DefaultHttpContext context = Create(body);
		context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature(body));

		return context;
	}

	/// <summary>
	/// Reads the UTF-8 text written to a response body stream.
	/// </summary>
	/// <param name="body">The stream the response body wrote into.</param>
	/// <returns>The decoded body text.</returns>
	public static string ReadBody(Stream body) => Encoding.UTF8.GetString(((MemoryStream)body).ToArray());

	/// <summary>
	/// A response feature that reports the response as already started, so the write helpers take their
	/// header-committed skip path.
	/// </summary>
	private sealed class StartedResponseFeature(Stream body) : IHttpResponseFeature
	{
		public int StatusCode { get; set; } = 200;

		public string? ReasonPhrase { get; set; }

		public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

		public Stream Body { get; set; } = body;

		public bool HasStarted => true;

		public void OnStarting(Func<object, Task> callback, object state) { }

		public void OnCompleted(Func<object, Task> callback, object state) { }
	}
}
