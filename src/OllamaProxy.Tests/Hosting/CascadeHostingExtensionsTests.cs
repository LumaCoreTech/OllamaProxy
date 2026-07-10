// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

using OllamaProxy.Hosting;

namespace OllamaProxy.Tests.Hosting;

/// <summary>
/// Tests for <see cref="CascadeHostingExtensions"/>, the two-host cascade wiring. They drive the SCM-specific
/// and foreground branches deterministically through the injected <see cref="IServiceEnvironment"/> seam
/// (<see cref="FakeServiceEnvironment"/>) rather than depending on whether the test process itself runs under
/// the Service Control Manager.
/// </summary>
/// <remarks>
/// The file is organized by member, each in a <c>#region</c>:
/// <list type="number">
///     <item>
///         <description>
///         <c>AddOuterChassisHosting</c> — the writable-config path forks on the hosting model (foreground
///         writes beside the app under the content root; the service writes the ProgramData operator copy),
///         plus the null guard.
///         </description>
///     </item>
///     <item>
///         <description>
///         <c>AddInnerProxyHosting</c> — the data directory forks on the hosting model (foreground anchors at
///         the binary; the service routes to the ProgramData data subtree), plus the null guard.
///         </description>
///     </item>
///     <item>
///         <description>
///         <c>BuildProxyOptionsConfiguration</c> — the file-only admin snapshot reads the shipped defaults and,
///         by design, never surfaces environment variables, plus the argument guards.
///         </description>
///     </item>
/// </list>
/// </remarks>
[Trait("Category", "Unit")]
public sealed class CascadeHostingExtensionsTests : IDisposable
{
	private const string ProxyConfigFileName = "appsettings.json";
	private const string HostConfigFileName  = "hostsettings.json";
	private const string ProgramDataFolder   = "OllamaProxy";
	private const string DataFolder          = "data";

	private readonly string mContentRoot =
		Path.Combine(Path.GetTempPath(), $"ollamaproxy-cascade-{Guid.NewGuid():N}");

	/// <summary>
	/// Removes the isolated content-root directory and everything written into it once a test completes.
	/// </summary>
	public void Dispose()
	{
		if (Directory.Exists(mContentRoot))
			Directory.Delete(mContentRoot, recursive: true);
	}

	/// <summary>
	/// Resolves the absolute <c>%ProgramData%\OllamaProxy</c> root the service-mode branches target, computed the
	/// same way the production code does so the assertion tracks any relocation of the special folder.
	/// </summary>
	/// <returns>The absolute ProgramData application root.</returns>
	private static string ProgramDataRoot() => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
		ProgramDataFolder);

	/// <summary>
	/// Determines whether a configuration source is the JSON overlay for the file at
	/// <paramref name="directory"/> combined with <paramref name="fileName"/>.
	/// </summary>
	/// <param name="source">The configuration source to test.</param>
	/// <param name="directory">The directory the overlay file is expected to be rooted in.</param>
	/// <param name="fileName">The overlay file name (for example <c>appsettings.json</c>).</param>
	/// <returns><see langword="true"/> when the source is the expected overlay file; otherwise <see langword="false"/>.</returns>
	/// <remarks>
	/// The production code passes an absolute path to <c>ResolveFileProvider()</c>, which splits it between the
	/// <see cref="PhysicalFileProvider"/> root and <see cref="FileConfigurationSource.Path"/> at the
	/// <em>deepest directory that already exists on disk</em>: it climbs the tree from the file upward, roots the
	/// provider at the first existing directory, and leaves the remaining (non-existent) segments in
	/// <see cref="FileConfigurationSource.Path"/>. Where the split falls therefore depends on which directories
	/// happen to exist on the test machine — on a developer box <c>%ProgramData%\OllamaProxy</c> may already
	/// exist (root = that folder, path = the bare file name), while on a clean CI agent it does not (root =
	/// <c>%ProgramData%</c>, path = <c>OllamaProxy\{fileName}</c>). The overlay is therefore identified by
	/// recombining the provider root with the relative path and comparing that full path against the expected
	/// one, never by assuming a particular split.
	/// </remarks>
	private static bool IsJsonOverlayFor(IConfigurationSource source, string directory, string fileName)
	{
		if (source is not JsonConfigurationSource json ||
		    json.Path is null ||
		    json.FileProvider is not PhysicalFileProvider physical)
		{
			return false;
		}

		// Recombine the provider root with the (possibly multi-segment) relative path to recover the full path
		// the source actually resolves, regardless of where ResolveFileProvider() drew the root/path boundary.
		// Path.GetFullPath() then canonicalizes both sides (separators, trailing slash, symlinks, case) so the
		// comparison is stable across platforms and independent of which directories exist on the agent.
		string actualFullPath = Path.GetFullPath(Path.Combine(physical.Root, json.Path));
		string expectedFullPath = Path.GetFullPath(Path.Combine(directory, fileName));

		return string.Equals(actualFullPath, expectedFullPath, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Asserts that a JSON overlay source for <paramref name="fileName"/> rooted in <paramref name="directory"/>
	/// is present in <paramref name="sources"/> and ordered <em>before</em> the environment-variables source, the
	/// layering contract that makes an operator's ProgramData overlay override the shipped defaults while still
	/// losing to an environment variable. Verifying the source ordering (rather than a bound value) is what pins
	/// the service-branch overlay wiring deterministically off the Service Control Manager.
	/// </summary>
	/// <param name="sources">The configuration sources to inspect.</param>
	/// <param name="directory">The directory the overlay file is expected to be rooted in.</param>
	/// <param name="fileName">The overlay file name expected to be layered in.</param>
	private static void AssertOverlayLayeredBeforeEnvironmentVariables(
		IList<IConfigurationSource> sources,
		string                      directory,
		string                      fileName)
	{
		int overlayIndex = -1;
		int environmentIndex = -1;

		for (int index = 0; index < sources.Count; index++)
		{
			if (IsJsonOverlayFor(sources[index], directory, fileName))
			{
				overlayIndex = index;
			}
			else if (sources[index] is EnvironmentVariablesConfigurationSource)
			{
				environmentIndex = index;
			}
		}

		Assert.True(overlayIndex >= 0, $"Expected a JSON overlay source for '{fileName}' rooted in '{directory}'.");
		Assert.True(environmentIndex >= 0, "Expected an environment-variables source.");
		Assert.True(
			overlayIndex < environmentIndex,
			"The ProgramData overlay must be layered before the environment-variables source so files lose to env vars.");
	}

	/// <summary>
	/// Creates a minimal <see cref="WebApplicationBuilder"/> rooted at this test's isolated content-root
	/// directory, so the hosting extensions resolve foreground paths against a disposable location instead of the
	/// test host's own directory.
	/// </summary>
	/// <returns>A builder whose content root is the test's temp directory.</returns>
	private WebApplicationBuilder CreateBuilder()
	{
		Directory.CreateDirectory(mContentRoot);

		// AddOuterChassisHosting replaces the proxy's JSON sources with a NON-optional hostsettings.json under
		// the content root, so it must exist on disk or the configuration reload throws. Seed a minimal file.
		File.WriteAllText(Path.Combine(mContentRoot, "hostsettings.json"), "{ }");

		return WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = mContentRoot });
	}

	#region AddOuterChassisHosting

	/// <summary>
	/// Verifies that foreground hosting points the writable operator configuration at <c>appsettings.json</c>
	/// beside the application under the content root, preserving the "files live beside the app" behavior.
	/// </summary>
	[Fact]
	public void AddOuterChassisHosting_WhenForeground_WritesOperatorConfigUnderContentRoot()
	{
		// Arrange
		WebApplicationBuilder builder = CreateBuilder();

		// Act
		builder.AddOuterChassisHosting(FakeServiceEnvironment.Foreground);

		// Assert
		using ServiceProvider provider = builder.Services.BuildServiceProvider();
		var file = provider.GetRequiredService<IWritableProxyConfigFile>();
		Assert.Equal(Path.Combine(mContentRoot, ProxyConfigFileName), file.Path);
	}

	/// <summary>
	/// Verifies that under the Service Control Manager the writable operator configuration targets the
	/// <c>%ProgramData%\OllamaProxy</c> copy, because the install directory is read-only to the service account.
	/// </summary>
	[Fact]
	public void AddOuterChassisHosting_WhenWindowsService_WritesOperatorConfigUnderProgramData()
	{
		// Arrange
		WebApplicationBuilder builder = CreateBuilder();

		// Act
		builder.AddOuterChassisHosting(FakeServiceEnvironment.Service);

		// Assert
		using ServiceProvider provider = builder.Services.BuildServiceProvider();
		var file = provider.GetRequiredService<IWritableProxyConfigFile>();
		Assert.Equal(Path.Combine(ProgramDataRoot(), ProxyConfigFileName), file.Path);
	}

	/// <summary>
	/// Verifies that under the Service Control Manager the outer chassis layers the operator copy of
	/// <c>hostsettings.json</c> from <c>%ProgramData%\OllamaProxy</c> ahead of the environment-variables source,
	/// so the operator overlay overrides the shipped chassis defaults yet still yields to an environment
	/// override. This pins the service-branch overlay wiring, not just the resolved paths.
	/// </summary>
	[Fact]
	public void AddOuterChassisHosting_WhenWindowsService_LayersProgramDataHostOverlayBeforeEnvironmentVariables()
	{
		// Arrange
		WebApplicationBuilder builder = CreateBuilder();

		// Act
		builder.AddOuterChassisHosting(FakeServiceEnvironment.Service);

		// Assert
		AssertOverlayLayeredBeforeEnvironmentVariables(
			builder.Configuration.Sources,
			ProgramDataRoot(),
			HostConfigFileName);
	}

	/// <summary>
	/// Verifies that foreground hosting adds no ProgramData chassis overlay, so a console / container run reads
	/// only the shipped <c>hostsettings.json</c> and never the service-only operator copy.
	/// </summary>
	[Fact]
	public void AddOuterChassisHosting_WhenForeground_DoesNotLayerProgramDataHostOverlay()
	{
		// Arrange
		WebApplicationBuilder builder = CreateBuilder();

		// Act
		builder.AddOuterChassisHosting(FakeServiceEnvironment.Foreground);

		// Assert: no configuration source points at the ProgramData chassis overlay.
		Assert.DoesNotContain(
			builder.Configuration.Sources,
			source => IsJsonOverlayFor(source, ProgramDataRoot(), HostConfigFileName));
	}

	/// <summary>
	/// Verifies that <see cref="CascadeHostingExtensions.AddOuterChassisHosting"/> rejects a
	/// <see langword="null"/> builder.
	/// </summary>
	[Fact]
	public void AddOuterChassisHosting_WhenBuilderIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() =>
			CascadeHostingExtensions.AddOuterChassisHosting(null!));
		Assert.Equal("builder", exception.ParamName);
	}

	#endregion

	#region AddInnerProxyHosting

	/// <summary>
	/// Verifies that foreground hosting anchors the data directory at the executable's own directory
	/// (<see cref="AppContext.BaseDirectory"/>) so runtime artifacts land beside the binary, not in the source tree.
	/// </summary>
	[Fact]
	public void AddInnerProxyHosting_WhenForeground_AnchorsDataDirectoryAtBaseDirectory()
	{
		// Arrange
		WebApplicationBuilder builder = CreateBuilder();

		// Act
		builder.AddInnerProxyHosting(FakeServiceEnvironment.Foreground);

		// Assert
		using ServiceProvider provider = builder.Services.BuildServiceProvider();
		var dataDirectory = provider.GetRequiredService<IDataDirectory>();
		Assert.Equal(AppContext.BaseDirectory, dataDirectory.BasePath);
	}

	/// <summary>
	/// Verifies that under the Service Control Manager the data directory routes to the writable
	/// <c>%ProgramData%\OllamaProxy\data</c> subtree the installer grants the service account modify rights on.
	/// </summary>
	[Fact]
	public void AddInnerProxyHosting_WhenWindowsService_RoutesDataDirectoryToProgramDataSubtree()
	{
		// Arrange
		WebApplicationBuilder builder = CreateBuilder();

		// Act
		builder.AddInnerProxyHosting(FakeServiceEnvironment.Service);

		// Assert
		using ServiceProvider provider = builder.Services.BuildServiceProvider();
		var dataDirectory = provider.GetRequiredService<IDataDirectory>();
		Assert.Equal(Path.Combine(ProgramDataRoot(), DataFolder), dataDirectory.BasePath);
	}

	/// <summary>
	/// Verifies that under the Service Control Manager the inner proxy host layers the operator copy of
	/// <c>appsettings.json</c> from <c>%ProgramData%\OllamaProxy</c> ahead of the environment-variables source,
	/// so the operator overlay overrides the shipped proxy defaults yet still yields to an environment override.
	/// This pins the service-branch overlay wiring, not just the resolved data-directory path.
	/// </summary>
	[Fact]
	public void AddInnerProxyHosting_WhenWindowsService_LayersProgramDataProxyOverlayBeforeEnvironmentVariables()
	{
		// Arrange
		WebApplicationBuilder builder = CreateBuilder();

		// Act
		builder.AddInnerProxyHosting(FakeServiceEnvironment.Service);

		// Assert
		AssertOverlayLayeredBeforeEnvironmentVariables(
			builder.Configuration.Sources,
			ProgramDataRoot(),
			ProxyConfigFileName);
	}

	/// <summary>
	/// Verifies that foreground hosting adds no ProgramData proxy overlay, so a console / container run reads
	/// only the shipped <c>appsettings.json</c> and never the service-only operator copy.
	/// </summary>
	[Fact]
	public void AddInnerProxyHosting_WhenForeground_DoesNotLayerProgramDataProxyOverlay()
	{
		// Arrange
		WebApplicationBuilder builder = CreateBuilder();

		// Act
		builder.AddInnerProxyHosting(FakeServiceEnvironment.Foreground);

		// Assert: the only appsettings.json sources are the shipped/content-root ones, none under ProgramData.
		Assert.DoesNotContain(
			builder.Configuration.Sources,
			source => IsJsonOverlayFor(source, ProgramDataRoot(), ProxyConfigFileName));
	}

	/// <summary>
	/// Verifies that <see cref="CascadeHostingExtensions.AddInnerProxyHosting"/> rejects a
	/// <see langword="null"/> builder.
	/// </summary>
	[Fact]
	public void AddInnerProxyHosting_WhenBuilderIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() =>
			CascadeHostingExtensions.AddInnerProxyHosting(null!));
		Assert.Equal("builder", exception.ParamName);
	}

	#endregion

	#region BuildProxyOptionsConfiguration

	/// <summary>
	/// Verifies that the admin snapshot reads the shipped <c>appsettings.json</c> under the content root, so the
	/// admin surface binds the same backend defaults the inner proxy loads from disk.
	/// </summary>
	[Fact]
	public void BuildProxyOptionsConfiguration_WhenShippedFileExists_ReadsItsValues()
	{
		// Arrange
		Directory.CreateDirectory(mContentRoot);
		File.WriteAllText(
			Path.Combine(mContentRoot, ProxyConfigFileName),
			"""{ "OllamaProxy": { "ListenUrl": "http://localhost:9999" } }""");

		// Act
		IConfigurationRoot configuration = CascadeHostingExtensions.BuildProxyOptionsConfiguration(
			mContentRoot,
			"Production",
			FakeServiceEnvironment.Foreground);

		// Assert
		Assert.Equal("http://localhost:9999", configuration["OllamaProxy:ListenUrl"]);
	}

	/// <summary>
	/// Verifies that the admin snapshot deliberately omits environment variables so it reflects exactly what is
	/// on disk: an <c>OllamaProxy__</c> environment override that the running proxy would honor must not bleed
	/// into the file-only admin view.
	/// </summary>
	[Fact]
	public void BuildProxyOptionsConfiguration_DoesNotSurfaceEnvironmentVariables()
	{
		// Arrange: no file on disk, but an environment override the inner host would otherwise layer on top.
		Directory.CreateDirectory(mContentRoot);
		string variable = $"OllamaProxy__ListenUrl__{Guid.NewGuid():N}";
		Environment.SetEnvironmentVariable(variable, "http://env-should-not-appear");

		try
		{
			// Act
			IConfigurationRoot configuration = CascadeHostingExtensions.BuildProxyOptionsConfiguration(
				mContentRoot,
				"Production",
				FakeServiceEnvironment.Foreground);

			// Assert: the file-only view never read the process environment, so no OllamaProxy key is present.
			Assert.Null(configuration["OllamaProxy:ListenUrl"]);
		}
		finally
		{
			Environment.SetEnvironmentVariable(variable, null);
		}
	}

	/// <summary>
	/// Verifies that <see cref="CascadeHostingExtensions.BuildProxyOptionsConfiguration"/> rejects a
	/// <see langword="null"/> content root.
	/// </summary>
	[Fact]
	public void BuildProxyOptionsConfiguration_WhenContentRootIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() =>
			CascadeHostingExtensions.BuildProxyOptionsConfiguration(null!, "Production"));
		Assert.Equal("contentRootPath", exception.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="CascadeHostingExtensions.BuildProxyOptionsConfiguration"/> rejects an empty or
	/// white-space content root.
	/// </summary>
	/// <param name="contentRootPath">The invalid content-root path under test.</param>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void BuildProxyOptionsConfiguration_WhenContentRootIsEmptyOrWhiteSpace_ThrowsArgumentException(
		string contentRootPath)
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentException>(() =>
			CascadeHostingExtensions.BuildProxyOptionsConfiguration(contentRootPath, "Production"));
		Assert.Equal("contentRootPath", exception.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="CascadeHostingExtensions.BuildProxyOptionsConfiguration"/> rejects a
	/// <see langword="null"/> environment name.
	/// </summary>
	[Fact]
	public void BuildProxyOptionsConfiguration_WhenEnvironmentNameIsNull_ThrowsArgumentNullException()
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentNullException>(() =>
			CascadeHostingExtensions.BuildProxyOptionsConfiguration("C:\\content-root", null!));
		Assert.Equal("environmentName", exception.ParamName);
	}

	/// <summary>
	/// Verifies that <see cref="CascadeHostingExtensions.BuildProxyOptionsConfiguration"/> rejects an empty or
	/// white-space environment name.
	/// </summary>
	/// <param name="environmentName">The invalid environment name under test.</param>
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void BuildProxyOptionsConfiguration_WhenEnvironmentNameIsEmptyOrWhiteSpace_ThrowsArgumentException(
		string environmentName)
	{
		// Act + Assert
		var exception = Assert.Throws<ArgumentException>(() =>
			CascadeHostingExtensions.BuildProxyOptionsConfiguration("C:\\content-root", environmentName));
		Assert.Equal("environmentName", exception.ParamName);
	}

	#endregion
}
