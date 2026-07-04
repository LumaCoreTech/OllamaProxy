// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using OllamaProxy.Core;
using OllamaProxy.Hosting.Cascade;

namespace OllamaProxy.Tests.Hosting.Cascade;

/// <summary>
/// Shared fakes and setup helpers for <see cref="ProxyHostSupervisorTests"/>. The fakes stand in for the
/// inner proxy host and its factory so the supervisor's lifecycle can be exercised without binding a real
/// port or running real model discovery.
/// </summary>
public sealed partial class ProxyHostSupervisorTests
{
	/// <summary>
	/// Creates a supervisor wired to the supplied factory and a no-op logger, defaulting to the daemon policy
	/// (a start failure stays resident) unless <paramref name="failFastOnStartFailure"/> selects the foreground
	/// policy (a start failure rethrows).
	/// </summary>
	/// <param name="factory">The host factory the supervisor builds inner hosts through.</param>
	/// <param name="failFastOnStartFailure">Whether an initial start failure should rethrow (foreground policy).</param>
	/// <returns>A configured <see cref="ProxyHostSupervisor"/> ready to drive in a test.</returns>
	private static ProxyHostSupervisor CreateSut(
		IProxyHostFactory factory,
		bool              failFastOnStartFailure = false) => new(
		factory,
		failFastOnStartFailure,
		NullLogger<ProxyHostSupervisor>.Instance);

	/// <summary>
	/// Selects which kind of failure a dry-run candidate raises, so the recycle-failure theory can drive both
	/// the options-validation extraction path and the generic-exception fallback from serializable theory data.
	/// </summary>
	public enum DryRunFailureKind
	{
		/// <summary>
		/// The candidate fails with an <see cref="Microsoft.Extensions.Options.OptionsValidationException"/>.
		/// </summary>
		OptionsValidation,

		/// <summary>
		/// The candidate fails with a generic exception whose message is surfaced verbatim.
		/// </summary>
		Generic
	}

	/// <summary>
	/// A test double for the inner proxy host: it records the lifecycle calls the supervisor makes and can be
	/// configured to fault its start, standing in for a host whose configuration fails validation or whose
	/// server fails to bind. It implements <see cref="IAsyncDisposable"/> so the supervisor's preferred
	/// asynchronous disposal path is exercised, exactly as a real <c>WebApplication</c> would be.
	/// </summary>
	internal sealed class FakeProxyHost : IHost, IAsyncDisposable
	{
		private readonly Exception?        mStartException;
		private readonly IServiceProvider? mServices;

		/// <summary>
		/// Initializes a new instance of the <see cref="FakeProxyHost"/> class.
		/// </summary>
		/// <param name="startException">
		/// The exception <see cref="StartAsync"/> should fault with, or <see langword="null"/> for a host that
		/// starts successfully.
		/// </param>
		/// <param name="services">
		/// The service provider <see cref="Services"/> should expose, or <see langword="null"/> to keep the
		/// default behavior of throwing on access (for the lifecycle tests that never resolve a service).
		/// </param>
		public FakeProxyHost(Exception? startException = null, IServiceProvider? services = null)
		{
			mStartException = startException;
			mServices = services;
		}

		/// <summary>
		/// Gets the number of times <see cref="StartAsync"/> was invoked.
		/// </summary>
		public int StartCallCount { get; private set; }

		/// <summary>
		/// Gets the number of times <see cref="StopAsync"/> was invoked.
		/// </summary>
		public int StopCallCount { get; private set; }

		/// <summary>
		/// Gets a value indicating whether the host was started at least once.
		/// </summary>
		public bool StartCalled => StartCallCount > 0;

		/// <summary>
		/// Gets a value indicating whether the host was stopped at least once.
		/// </summary>
		public bool StopCalled => StopCallCount > 0;

		/// <summary>
		/// Gets a value indicating whether the host was disposed through either disposal contract.
		/// </summary>
		public bool Disposed { get; private set; }

		/// <summary>
		/// The host's service provider. When one was supplied at construction (the live-catalog tests) it is
		/// returned; otherwise accessing it signals an unexpected new dependency and fails the lifecycle tests
		/// loudly rather than silently returning an empty provider.
		/// </summary>
		public IServiceProvider Services => mServices ??
		                                    throw new NotSupportedException(
			                                    "FakeProxyHost.Services is not used by the supervisor under test.");

		/// <inheritdoc/>
		public Task StartAsync(CancellationToken cancellationToken = default)
		{
			StartCallCount++;

			return mStartException is null ? Task.CompletedTask : Task.FromException(mStartException);
		}

		/// <inheritdoc/>
		public Task StopAsync(CancellationToken cancellationToken = default)
		{
			StopCallCount++;

			return Task.CompletedTask;
		}

		/// <inheritdoc/>
		public ValueTask DisposeAsync()
		{
			Disposed = true;

			return ValueTask.CompletedTask;
		}

		/// <inheritdoc/>
		public void Dispose() => Disposed = true;
	}

	/// <summary>
	/// A test double for <see cref="IProxyHostFactory"/> that hands out pre-seeded <see cref="FakeProxyHost"/>
	/// instances from two independent queues — one for real-server builds and one for dry-run builds — so a test
	/// can script the exact sequence of hosts a start or recycle will receive and inspect them afterward.
	/// </summary>
	internal sealed class FakeProxyHostFactory : IProxyHostFactory
	{
		private readonly Queue<FakeProxyHost> mRealHosts   = new();
		private readonly Queue<FakeProxyHost> mDryRunHosts = new();

		/// <summary>
		/// Gets the number of real-server hosts the supervisor has requested.
		/// </summary>
		public int RealHostRequestCount { get; private set; }

		/// <summary>
		/// Gets the number of dry-run hosts the supervisor has requested.
		/// </summary>
		public int DryRunHostRequestCount { get; private set; }

		/// <summary>
		/// Seeds the next real-server host the factory will hand out (the host returned for
		/// <c>useDryRunServer: false</c>).
		/// </summary>
		/// <param name="host">The host to enqueue.</param>
		/// <returns>The same factory instance, to support fluent setup.</returns>
		public FakeProxyHostFactory EnqueueRealHost(FakeProxyHost host)
		{
			mRealHosts.Enqueue(host);

			return this;
		}

		/// <summary>
		/// Seeds the next dry-run host the factory will hand out (the host returned for
		/// <c>useDryRunServer: true</c>).
		/// </summary>
		/// <param name="host">The host to enqueue.</param>
		/// <returns>The same factory instance, to support fluent setup.</returns>
		public FakeProxyHostFactory EnqueueDryRunHost(FakeProxyHost host)
		{
			mDryRunHosts.Enqueue(host);

			return this;
		}

		/// <inheritdoc/>
		public IHost CreateProxyHost(bool useDryRunServer)
		{
			if (useDryRunServer)
			{
				DryRunHostRequestCount++;

				return mDryRunHosts.Dequeue();
			}

			RealHostRequestCount++;

			return mRealHosts.Dequeue();
		}
	}

	/// <summary>
	/// A minimal <see cref="IModelRouter"/> standing in for the inner host's catalog, returning a fixed model
	/// list so the supervisor's live-catalog read can be exercised without running real discovery.
	/// </summary>
	internal sealed class StubModelRouter : IModelRouter
	{
		private readonly IReadOnlyList<RegisteredModel> mModels;

		/// <summary>Initializes the stub with the catalog it should report.</summary>
		/// <param name="models">The models <see cref="GetModels"/> returns.</param>
		public StubModelRouter(IReadOnlyList<RegisteredModel> models)
		{
			mModels = models;
		}

		/// <inheritdoc/>
		public IReadOnlyList<RegisteredModel> GetModels() => mModels;

		/// <inheritdoc/>
		public bool TryResolve(string modelName, [NotNullWhen(true)] out RegisteredModel? model)
		{
			model = null;

			return false;
		}
	}

	/// <summary>
	/// Builds a <see cref="FakeProxyHost"/> whose service provider resolves an <see cref="IModelRouter"/>
	/// reporting the supplied models, so the supervisor's <see cref="ProxyHostSupervisor.GetLiveModels"/> reads
	/// them as the live catalog.
	/// </summary>
	/// <param name="models">The models the host's router should report.</param>
	/// <returns>A started-capable fake host exposing a router over <paramref name="models"/>.</returns>
	private static FakeProxyHost CreateHostWithCatalog(IReadOnlyList<RegisteredModel> models)
	{
		ServiceProvider provider = new ServiceCollection()
			.AddSingleton<IModelRouter>(new StubModelRouter(models))
			.BuildServiceProvider();

		return new FakeProxyHost(startException: null, services: provider);
	}
}
