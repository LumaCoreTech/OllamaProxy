// Copyright (c) 2026 LumaCoreTech
// SPDX-License-Identifier: MIT
// Project: https://github.com/LumaCoreTech/OllamaProxy

using System;
using System.Globalization;
using System.IO;
using System.Text;

using WixToolset.Dtf.WindowsInstaller;

namespace OllamaProxy.CustomActions;

public static partial class CustomActions
{
	/// <summary>
	/// Writes the operator's configuration into <c>%ProgramData%\OllamaProxy\</c>. Runs deferred and
	/// elevated, after the files are installed, so it can write into the protected ProgramData location.
	/// Two files are maintained:
	/// <list type="bullet">
	///     <item><c>appsettings.json</c> — the proxy engine configuration (backend, API key, listener URL).</item>
	///     <item><c>hostsettings.json</c> — the outer chassis configuration (admin endpoint URL).</item>
	/// </list>
	/// On an upgrade or repair both files are left untouched so operator edits survive; on a genuine fresh
	/// install the wizard's values are written, and any leftover files from a previous installation are
	/// first backed up under timestamped names so nothing is lost silently. All values, including the
	/// fresh-install discriminator, arrive through the deferred action's <c>CustomActionData</c>.
	/// </summary>
	/// <param name="session">The running installer session carrying the deferred data.</param>
	/// <returns>
	/// <see cref="ActionResult.Success"/> when the configuration was written or deliberately preserved;
	/// <see cref="ActionResult.Failure"/> if an existing file could not be backed up or a new file
	/// could not be created.
	/// </returns>
	[CustomAction]
	public static ActionResult WriteAppSettings(Session session)
	{
		if (session == null) throw new ArgumentNullException(nameof(session));

		CustomActionData data = session.CustomActionData;
		string configPath = GetData(data, "AppCfg");
		string hostConfigPath = GetData(data, "HostCfg");
		string baseUrl = GetData(data, "Url");
		string providerType = GetData(data, "Prov");
		string apiKey = GetData(data, "Key");
		string listenUrl = GetData(data, "Listen");
		string adminUrl = GetData(data, "Admin");
		bool freshInstall = GetData(data, "Fresh") == "1";

		bool configExists = File.Exists(configPath);
		bool hostConfigExists = File.Exists(hostConfigPath);
		ConfigWriteAction action = DecideConfigWriteAction(freshInstall, configExists || hostConfigExists);

		if (action == ConfigWriteAction.Preserve)
		{
			// Upgrade or repair: only the binaries are refreshed. The operator's existing configuration
			// (and any hand edits) must survive untouched — and a silent upgrade carrying the empty key
			// default must never replace a valid config, so we preserve even when no file is present.
			session.Log("OllamaProxy: preserving existing configuration at '{0}' (upgrade or repair).", configPath);
			return ActionResult.Success;
		}

		try
		{
			string directory = Path.GetDirectoryName(configPath);
			if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

			string hostDirectory = Path.GetDirectoryName(hostConfigPath);
			if (!string.IsNullOrEmpty(hostDirectory)) Directory.CreateDirectory(hostDirectory);

			if (action == ConfigWriteAction.BackUpAndWrite)
			{
				// A leftover config from a previous installation (the data folder survives uninstall by
				// design). Move it aside under a timestamped name before writing the new one, so repeated
				// reinstalls accumulate distinct backups instead of clobbering one another.
				if (configExists)
				{
					string backupPath = BackUpExistingConfig(configPath);
					session.Log("OllamaProxy: backed up the previous appsettings.json to '{0}'.", backupPath);
				}

				if (hostConfigExists)
				{
					string backupPath = BackUpExistingConfig(hostConfigPath);
					session.Log("OllamaProxy: backed up the previous hostsettings.json to '{0}'.", backupPath);
				}
			}

			session.Log("OllamaProxy: writing configuration to '{0}' and '{1}'.", configPath, hostConfigPath);

			string appSettingsJson = BuildAppSettings(listenUrl, baseUrl, providerType, apiKey);
			string hostSettingsJson = BuildHostSettings(adminUrl);

			// UTF-8 without a BOM: the .NET configuration provider reads it cleanly and it matches the
			// shipped appsettings.json encoding.
			File.WriteAllText(configPath, appSettingsJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			File.WriteAllText(
				hostConfigPath,
				hostSettingsJson,
				new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

			session.Log("OllamaProxy: configuration written successfully.");
			return ActionResult.Success;
		}
		catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
		{
			session.Log("OllamaProxy: failed to write configuration: {0}", exception.Message);
			return ActionResult.Failure;
		}
	}

	/// <summary>
	/// Decides what the deferred writer should do with <c>appsettings.json</c>, separating the three
	/// cases the installer must distinguish: preserve an operator's configuration on an upgrade or
	/// repair, write the wizard's values on a first install, or back up a leftover file before writing
	/// on a reinstall over a data folder that survived a prior uninstall.
	/// </summary>
	/// <param name="freshInstall">
	/// <see langword="true"/> when this is a genuine first install — neither a maintenance/repair run
	/// nor a major upgrade — as determined by the installer's <c>OLLAMAPROXY_FRESHINSTALL</c> property.
	/// </param>
	/// <param name="configExists">Whether a configuration file is already present at the target path.</param>
	/// <returns>The action the writer should take.</returns>
	internal static ConfigWriteAction DecideConfigWriteAction(bool freshInstall, bool configExists)
	{
		// Upgrade or repair: always preserve, regardless of whether a file is present. Only a genuine
		// fresh install (re)writes the wizard's values; an existing file is backed up first.
		if (!freshInstall) return ConfigWriteAction.Preserve;

		return configExists ? ConfigWriteAction.BackUpAndWrite : ConfigWriteAction.Write;
	}

	/// <summary>
	/// Builds the timestamped backup path for an existing configuration file: <c>appsettings.json</c>
	/// becomes, for example, <c>appsettings.20260607-001530.bak</c>. The timestamp is UTC and uses a
	/// sortable, filename-safe layout so backups order chronologically in a directory listing.
	/// </summary>
	/// <param name="configPath">The full path of the configuration file to back up.</param>
	/// <param name="timestampUtc">The UTC instant to stamp into the backup name.</param>
	/// <returns>The full backup path alongside <paramref name="configPath"/>.</returns>
	internal static string BuildBackupPath(string configPath, DateTime timestampUtc)
	{
		string directory = Path.GetDirectoryName(configPath);
		string baseName = Path.GetFileNameWithoutExtension(configPath);
		string stamp = timestampUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
		string backupName = baseName + "." + stamp + ".bak";

		return string.IsNullOrEmpty(directory) ? backupName : Path.Combine(directory, backupName);
	}

	/// <summary>
	/// Moves the existing configuration aside to a timestamped backup and returns the path it was moved
	/// to. If a backup with the same one-second-resolution timestamp already exists (two reinstalls
	/// within the same second), a numeric suffix is appended so an earlier backup is never overwritten.
	/// </summary>
	/// <param name="configPath">The configuration file to move aside.</param>
	/// <returns>The path the configuration was backed up to.</returns>
	private static string BackUpExistingConfig(string configPath)
	{
		string backupPath = BuildBackupPath(configPath, DateTime.UtcNow);
		if (File.Exists(backupPath)) backupPath = MakeUniqueBackupPath(backupPath);

		// Move (not copy): the original path is then free for the fresh file written immediately after.
		File.Move(configPath, backupPath);

		return backupPath;
	}

	/// <summary>
	/// Derives a collision-free variant of a desired backup path by appending an incrementing numeric
	/// suffix (<c>-2</c>, <c>-3</c>, …) until an unused name is found. Reached only in the rare case of
	/// two reinstalls within the same one-second timestamp window.
	/// </summary>
	/// <param name="desiredPath">The timestamped backup path that already exists on disk.</param>
	/// <returns>A backup path that does not yet exist on disk.</returns>
	private static string MakeUniqueBackupPath(string desiredPath)
	{
		string directory = Path.GetDirectoryName(desiredPath);
		string baseName = Path.GetFileNameWithoutExtension(desiredPath);
		string extension = Path.GetExtension(desiredPath);

		for (int counter = 2;; counter++)
		{
			string candidateName = baseName + "-" + counter.ToString(CultureInfo.InvariantCulture) + extension;
			string candidate = string.IsNullOrEmpty(directory)
				                   ? candidateName
				                   : Path.Combine(directory, candidateName);

			if (!File.Exists(candidate)) return candidate;
		}
	}

	/// <summary>
	/// Builds the <c>appsettings.json</c> content seeded with the operator's backend and listener URL,
	/// mirroring the shipped Plug-and-Play quick start. Written with System.Text.Json unavailable on this
	/// target, so values are escaped explicitly through <see cref="EscapeJson"/>.
	/// </summary>
	/// <param name="listenUrl">The HTTP endpoint the inner proxy host listens on.</param>
	/// <param name="baseUrl">The backend base URL to embed.</param>
	/// <param name="providerType">The provider-type discriminator to embed.</param>
	/// <param name="apiKey">The API key to embed.</param>
	/// <returns>The serialized configuration document.</returns>
	// internal (not private): exercised directly by the Windows-only custom-action test project.
	internal static string BuildAppSettings(
		string listenUrl,
		string baseUrl,
		string providerType,
		string apiKey)
	{
		if (string.IsNullOrWhiteSpace(listenUrl)) listenUrl = "http://localhost:11434";
		if (string.IsNullOrWhiteSpace(providerType)) providerType = "openai";

		var builder = new StringBuilder();
		builder.Append("{\n");
		builder.Append("  \"Logging\": {\n");
		builder.Append("    \"LogLevel\": {\n");
		builder.Append("      \"Default\": \"Information\",\n");
		builder.Append("      \"Microsoft.AspNetCore\": \"Warning\"\n");
		builder.Append("    }\n");
		builder.Append("  },\n");
		builder.Append("  \"OllamaProxy\": {\n");
		builder.Append("    \"ListenUrl\": \"").Append(EscapeJson(listenUrl)).Append("\",\n");
		builder.Append("    \"Backends\": {\n");
		builder.Append("      \"default\": {\n");
		builder.Append("        \"BaseUrl\": \"").Append(EscapeJson(baseUrl)).Append("\",\n");
		builder.Append("        \"ProviderType\": \"").Append(EscapeJson(providerType)).Append("\",\n");
		builder.Append("        \"ApiKey\": \"").Append(EscapeJson(apiKey)).Append("\",\n");
		// Seed the backend in PlugAndPlay so the first run exposes every model the backend reports
		// without any registry curation. Mode is backend-local; an empty Models registry keeps the
		// document shape identical to the shipped appsettings.json.
		builder.Append("        \"Mode\": \"PlugAndPlay\",\n");
		builder.Append("        \"Models\": []\n");
		builder.Append("      }\n");
		builder.Append("    }\n");
		builder.Append("  }\n");
		builder.Append("}\n");

		return builder.ToString();
	}

	/// <summary>
	/// Builds the <c>hostsettings.json</c> content seeded with the operator's admin endpoint URL. The
	/// outer chassis listens on this address while the inner proxy engine is recycled beneath it.
	/// </summary>
	/// <param name="adminUrl">The HTTP endpoint the admin UI listens on.</param>
	/// <returns>The serialized configuration document.</returns>
	// internal (not private): exercised directly by the Windows-only custom-action test project.
	internal static string BuildHostSettings(string adminUrl)
	{
		if (string.IsNullOrWhiteSpace(adminUrl)) adminUrl = "http://localhost:11435";

		var builder = new StringBuilder();
		builder.Append("{\n");
		builder.Append("  \"Logging\": {\n");
		builder.Append("    \"LogLevel\": {\n");
		builder.Append("      \"Default\": \"Information\",\n");
		builder.Append("      \"Microsoft.AspNetCore\": \"Warning\"\n");
		builder.Append("    }\n");
		builder.Append("  },\n");
		builder.Append("  \"Host\": {\n");
		builder.Append("    \"Mode\": \"Auto\"\n");
		builder.Append("  },\n");
		builder.Append("  \"Admin\": {\n");
		builder.Append("    \"Enabled\": true,\n");
		builder.Append("    \"ListenUrl\": \"").Append(EscapeJson(adminUrl)).Append("\"\n");
		builder.Append("  }\n");
		builder.Append("}\n");

		return builder.ToString();
	}

	/// <summary>
	/// Escapes a string for embedding inside a JSON string literal, covering the control characters
	/// and the quote/backslash that would otherwise break the document or allow injection.
	/// </summary>
	/// <param name="value">The raw value to escape.</param>
	/// <returns>The escaped value, safe to place between JSON quotes.</returns>
	// internal (not private): exercised directly by the Windows-only custom-action test project.
	internal static string EscapeJson(string value)
	{
		if (string.IsNullOrEmpty(value)) return string.Empty;

		var builder = new StringBuilder(value.Length + 8);
		foreach (char character in value)
		{
			switch (character)
			{
				case '"':
					builder.Append("\\\"");
					break;

				case '\\':
					builder.Append("\\\\");
					break;

				case '\b':
					builder.Append("\\b");
					break;

				case '\f':
					builder.Append("\\f");
					break;

				case '\n':
					builder.Append("\\n");
					break;

				case '\r':
					builder.Append("\\r");
					break;

				case '\t':
					builder.Append("\\t");
					break;

				default:
					if (character < ' ')
					{
						builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
					}
					else
					{
						builder.Append(character);
					}

					break;
			}
		}

		return builder.ToString();
	}

	/// <summary>
	/// Reads a value from the deferred <see cref="CustomActionData"/>, returning an empty string when
	/// the key is absent so the caller can treat missing data uniformly.
	/// </summary>
	/// <param name="data">The deferred custom-action data.</param>
	/// <param name="key">The key to read.</param>
	/// <returns>The stored value, or an empty string.</returns>
	private static string GetData(CustomActionData data, string key) =>
		data.ContainsKey(key) ? data[key] : string.Empty;
}
