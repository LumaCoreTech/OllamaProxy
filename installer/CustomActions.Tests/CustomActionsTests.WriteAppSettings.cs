// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System;

using Xunit;

namespace OllamaProxy.CustomActions.Tests;

public partial class CustomActionsTests
{
	/// <summary>
	/// Tests for the pure helpers behind <see cref="CustomActions.WriteAppSettings"/>: the config-write
	/// policy decision, the timestamped backup-path builder, the two JSON document builders, and the JSON
	/// string escaper. The <see cref="CustomActions.WriteAppSettings"/> entry point itself is not covered
	/// here because it is bound to a live installer <c>Session</c> and the file system.
	/// </summary>
	[Trait("Category", "Unit")]
	public sealed class WriteAppSettings
	{
		#region DecideConfigWriteAction()

		/// <summary>
		/// Provides the four (freshInstall × configExists) combinations and the action each must yield. The
		/// expected action travels as its <c>nameof</c> string because <see cref="ConfigWriteAction"/> is an
		/// internal type and cannot appear in this public member's signature; the test body parses it back.
		/// </summary>
		public static TheoryData<string, bool, bool, string> DecideConfigWriteActionCases => new()
		{
			// Upgrade or repair always preserves, regardless of whether a config file is present.
			{ "upgrade/repair, no file present", false, false, nameof(ConfigWriteAction.Preserve) },
			{ "upgrade/repair, file present", false, true, nameof(ConfigWriteAction.Preserve) },
			// A genuine fresh install writes; a leftover file is backed up first.
			{ "fresh install, no file present", true, false, nameof(ConfigWriteAction.Write) },
			{ "fresh install, file present", true, true, nameof(ConfigWriteAction.BackUpAndWrite) }
		};

		/// <summary>
		/// Verifies that <see cref="CustomActions.DecideConfigWriteAction"/> maps each install/file
		/// combination to the correct write action.
		/// </summary>
		/// <param name="scenario">A readable description of the case under test.</param>
		/// <param name="freshInstall">Whether this is a genuine fresh install.</param>
		/// <param name="configExists">Whether a configuration file already exists.</param>
		/// <param name="expectedActionName">The <c>nameof</c> of the action the writer should take.</param>
		[Theory]
		[MemberData(nameof(DecideConfigWriteActionCases))]
		public void DecideConfigWriteAction_ForInstallAndFileState_ReturnsExpectedAction(
			string scenario,
			bool   freshInstall,
			bool   configExists,
			string expectedActionName)
		{
			_ = scenario;

			// Arrange: reconstruct the internal enum here, where InternalsVisibleTo grants access.
			var expected = (ConfigWriteAction)Enum.Parse(typeof(ConfigWriteAction), expectedActionName);

			// Act
			ConfigWriteAction result = CustomActions.DecideConfigWriteAction(freshInstall, configExists);

			// Assert
			Assert.Equal(expected, result);
		}

		#endregion

		#region BuildBackupPath()

		/// <summary>
		/// Verifies that <see cref="CustomActions.BuildBackupPath"/> stamps a UTC timestamp into a sortable,
		/// filename-safe backup name alongside the original file, with and without a directory component.
		/// </summary>
		/// <param name="configPath">The configuration file path to back up.</param>
		/// <param name="expected">The expected timestamped backup path.</param>
		[Theory]
		[InlineData(
			@"C:\ProgramData\OllamaProxy\appsettings.json",
			@"C:\ProgramData\OllamaProxy\appsettings.20260607-001530.bak")]
		[InlineData("appsettings.json", "appsettings.20260607-001530.bak")]
		public void BuildBackupPath_WithTimestamp_ProducesSortableBackupName(string configPath, string expected)
		{
			// Arrange: a fixed UTC instant so the formatted stamp is deterministic.
			var timestamp = new DateTime(2026, 6, 7, 0, 15, 30, DateTimeKind.Utc);

			// Act
			string result = CustomActions.BuildBackupPath(configPath, timestamp);

			// Assert
			Assert.Equal(expected, result);
		}

		#endregion

		#region BuildAppSettings()

		/// <summary>
		/// Verifies that <see cref="CustomActions.BuildAppSettings"/> embeds all four supplied values into
		/// the expected Plug-and-Play document shape.
		/// </summary>
		[Fact]
		public void BuildAppSettings_WithAllValues_ProducesExpectedDocument()
		{
			// Act
			string result = CustomActions.BuildAppSettings(
				"http://localhost:11434",
				"https://api.openai.com/v1",
				"openai",
				"secret-key");

			// Assert
			Assert.Equal(
				ExpectedAppSettings("http://localhost:11434", "https://api.openai.com/v1", "openai", "secret-key"),
				result);
		}

		/// <summary>
		/// Verifies that a blank listener URL falls back to the default Ollama listener endpoint.
		/// </summary>
		[Fact]
		public void BuildAppSettings_WhenListenUrlBlank_DefaultsToOllamaPort()
		{
			// Act
			string result = CustomActions.BuildAppSettings(
				"   ",
				"https://api.openai.com/v1",
				"openai",
				"secret-key");

			// Assert: blank listener URL becomes http://localhost:11434.
			Assert.Equal(
				ExpectedAppSettings("http://localhost:11434", "https://api.openai.com/v1", "openai", "secret-key"),
				result);
		}

		/// <summary>
		/// Verifies that a blank provider type falls back to the default <c>openai</c> discriminator.
		/// </summary>
		[Fact]
		public void BuildAppSettings_WhenProviderTypeBlank_DefaultsToOpenai()
		{
			// Act
			string result = CustomActions.BuildAppSettings(
				"http://localhost:11434",
				"https://api.openai.com/v1",
				"   ",
				"secret-key");

			// Assert: blank provider type becomes openai.
			Assert.Equal(
				ExpectedAppSettings("http://localhost:11434", "https://api.openai.com/v1", "openai", "secret-key"),
				result);
		}

		/// <summary>
		/// Verifies that a value containing a JSON metacharacter is escaped where it is embedded, keeping the
		/// document well-formed.
		/// </summary>
		[Fact]
		public void BuildAppSettings_WhenApiKeyContainsQuote_EscapesValue()
		{
			// Act
			string result = CustomActions.BuildAppSettings(
				"http://localhost:11434",
				"https://api.openai.com/v1",
				"openai",
				"ab\"cd");

			// Assert: the embedded quote appears as \" in the document.
			Assert.Equal(
				ExpectedAppSettings("http://localhost:11434", "https://api.openai.com/v1", "openai", "ab\\\"cd"),
				result);
		}

		/// <summary>
		/// Builds the expected <c>appsettings.json</c> document for the given field values exactly as they
		/// should appear in the output (already escaped where relevant).
		/// </summary>
		/// <param name="listenUrl">The listener URL as it should appear in the document.</param>
		/// <param name="baseUrl">The backend base URL as it should appear in the document.</param>
		/// <param name="providerType">The provider type as it should appear in the document.</param>
		/// <param name="apiKey">The API key as it should appear in the document.</param>
		/// <returns>The expected serialized document.</returns>
		private static string ExpectedAppSettings(
			string listenUrl,
			string baseUrl,
			string providerType,
			string apiKey) => Doc(
			"{",
			"  \"Logging\": {",
			"    \"LogLevel\": {",
			"      \"Default\": \"Information\",",
			"      \"Microsoft.AspNetCore\": \"Warning\"",
			"    }",
			"  },",
			"  \"OllamaProxy\": {",
			"    \"ListenUrl\": \"" + listenUrl + "\",",
			"    \"Backends\": {",
			"      \"default\": {",
			"        \"BaseUrl\": \"" + baseUrl + "\",",
			"        \"ProviderType\": \"" + providerType + "\",",
			"        \"ApiKey\": \"" + apiKey + "\",",
			"        \"Mode\": \"PlugAndPlay\",",
			"        \"Models\": []",
			"      }",
			"    }",
			"  }",
			"}");

		#endregion

		#region BuildHostSettings()

		/// <summary>
		/// Verifies that <see cref="CustomActions.BuildHostSettings"/> embeds the supplied admin URL into the
		/// expected host document shape.
		/// </summary>
		[Fact]
		public void BuildHostSettings_WithAdminUrl_ProducesExpectedDocument()
		{
			// Act
			string result = CustomActions.BuildHostSettings("http://localhost:11435");

			// Assert
			Assert.Equal(ExpectedHostSettings("http://localhost:11435"), result);
		}

		/// <summary>
		/// Verifies that a blank admin URL falls back to the default admin endpoint on port 11435 (guarding
		/// the fixed fallback against a regression to any other port).
		/// </summary>
		[Fact]
		public void BuildHostSettings_WhenAdminUrlBlank_DefaultsToAdminPort()
		{
			// Act
			string result = CustomActions.BuildHostSettings("   ");

			// Assert: blank admin URL becomes http://localhost:11435.
			Assert.Equal(ExpectedHostSettings("http://localhost:11435"), result);
		}

		/// <summary>
		/// Builds the expected <c>hostsettings.json</c> document for the given admin URL exactly as it should
		/// appear in the output (already escaped where relevant).
		/// </summary>
		/// <param name="adminUrl">The admin URL as it should appear in the document.</param>
		/// <returns>The expected serialized document.</returns>
		private static string ExpectedHostSettings(string adminUrl) => Doc(
			"{",
			"  \"Logging\": {",
			"    \"LogLevel\": {",
			"      \"Default\": \"Information\",",
			"      \"Microsoft.AspNetCore\": \"Warning\"",
			"    }",
			"  },",
			"  \"Host\": {",
			"    \"Mode\": \"Auto\"",
			"  },",
			"  \"Admin\": {",
			"    \"Enabled\": true,",
			"    \"ListenUrl\": \"" + adminUrl + "\"",
			"  }",
			"}");

		#endregion

		#region EscapeJson()

		/// <summary>
		/// Provides the raw-to-escaped mappings <see cref="CustomActions.EscapeJson"/> must produce.
		/// </summary>
		public static TheoryData<string, string, string> EscapeJsonCases => new()
		{
			{ "empty string is returned unchanged", "", "" },
			{ "quote becomes escaped quote", "\"", "\\\"" },
			{ "backslash becomes double backslash", "\\", "\\\\" },
			{ "backspace becomes \\b", "\b", "\\b" },
			{ "form feed becomes \\f", "\f", "\\f" },
			{ "newline becomes \\n", "\n", "\\n" },
			{ "carriage return becomes \\r", "\r", "\\r" },
			{ "tab becomes \\t", "\t", "\\t" },
			{ "other control char becomes \\uXXXX", "\u0001", "\\u0001" },
			{ "printable text is returned unchanged", "Hello-World_123", "Hello-World_123" },
			{ "mixed content escapes only the metacharacters", "a\"b\\c", "a\\\"b\\\\c" }
		};

		/// <summary>
		/// Verifies that <see cref="CustomActions.EscapeJson"/> escapes the JSON metacharacters and control
		/// characters while leaving printable content untouched.
		/// </summary>
		/// <param name="scenario">A readable description of the case under test.</param>
		/// <param name="value">The raw value to escape.</param>
		/// <param name="expected">The expected escaped value.</param>
		[Theory]
		[MemberData(nameof(EscapeJsonCases))]
		public void EscapeJson_ForValue_ReturnsEscapedValue(string scenario, string value, string expected)
		{
			_ = scenario;

			// Act
			string result = CustomActions.EscapeJson(value);

			// Assert
			Assert.Equal(expected, result);
		}

		/// <summary>
		/// Verifies that a <see langword="null"/> value is treated as empty and returns an empty string.
		/// </summary>
		[Fact]
		public void EscapeJson_WhenNull_ReturnsEmpty()
		{
			// Act
			string result = CustomActions.EscapeJson(null);

			// Assert
			Assert.Equal(string.Empty, result);
		}

		#endregion

		#region Helpers

		/// <summary>
		/// Joins the given lines with the <c>\n</c> separator the document builders emit and appends a
		/// trailing newline, so expected documents are built independent of the source file's line endings
		/// (a CRLF-saved raw string literal would not match the builders' <c>\n</c> output).
		/// </summary>
		/// <param name="lines">The document lines, without separators.</param>
		/// <returns>The lines joined by <c>\n</c> with a trailing <c>\n</c>.</returns>
		private static string Doc(params string[] lines) => string.Join("\n", lines) + "\n";

		#endregion
	}
}
