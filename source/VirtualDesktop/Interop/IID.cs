using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.Linq;
using System.Text.RegularExpressions;
using WindowsDesktop.Properties;
using Microsoft.Win32;

namespace WindowsDesktop.Interop
{
	public static class IID
	{
		private static readonly Regex _osBuildRegex = new Regex(@"v_(?<build>\d{5}?)$");
		private static readonly Regex _osBuildOrLaterRegex = new Regex(@"v_(?<build>\d{5}?)_or_later");

		// ReSharper disable once InconsistentNaming
		public static Dictionary<string, Guid> GetIIDs(string[] targets)
		{
			var known = GetIIDsFromAppConfig(targets);

			var except = targets.Except(known.Keys).ToArray();
			if (except.Length > 0)
			{
				var fromRegistry = GetIIDsFromRegistry(except);
				foreach (var kvp in fromRegistry) known.Add(kvp.Key, kvp.Value);
			}

			return known;
		}

		// ReSharper disable once InconsistentNaming
		private static Dictionary<string, Guid> GetIIDsFromAppConfig(string[] targets)
		{
			var props = Settings.Default.Properties.OfType<SettingsProperty>();

			foreach (var prop in props)
			{
				if (int.TryParse(_osBuildRegex.Match(prop.Name).Groups["build"]?.ToString(), out var build)
					&& build == ProductInfo.OSBuild)
				{
					return ParseIIDsFromSettingsProperty(targets, prop);
				}
			}

			foreach (var prop in props)
			{
				if (int.TryParse(_osBuildOrLaterRegex.Match(prop.Name).Groups["build"]?.ToString(), out var laterBuild)
					&& laterBuild <= ProductInfo.OSBuild)
				{
					return ParseIIDsFromSettingsProperty(targets, prop);
				}
			}

			return new Dictionary<string, Guid>();
		}

		// ReSharper disable once InconsistentNaming
		private static Dictionary<string, Guid> ParseIIDsFromSettingsProperty(string[] targets, SettingsProperty prop)
		{
			var known = new Dictionary<string, Guid>();
			foreach (var str in (StringCollection)Settings.Default[prop.Name])
			{
				var pair = str.Split(',');
				if (pair.Length != 2) continue;

				var @interface = pair[0];
				if (targets.All(x => @interface != x) || known.ContainsKey(@interface)) continue;

				if (!Guid.TryParse(pair[1], out var guid)) continue;

				known.Add(@interface, guid);
			}

			return known;
		}

		// ReSharper disable once InconsistentNaming
		private static Dictionary<string, Guid> GetIIDsFromRegistry(string[] targets)
		{
			using (var interfaceKey = Registry.ClassesRoot.OpenSubKey("Interface"))
			{
				if (interfaceKey == null)
				{
					throw new Exception(@"Registry key '\HKEY_CLASSES_ROOT\Interface' is missing.");
				}

				var result = new Dictionary<string, Guid>();
				var names = interfaceKey.GetSubKeyNames();

				foreach (var name in names)
				{
					using (var key = interfaceKey.OpenSubKey(name))
					{
						if (key?.GetValue("") is string value)
						{
							var match = targets.FirstOrDefault(x => x == value);
							if (match != null && Guid.TryParse(key.Name.Split('\\').Last(), out var guid))
							{
								result[match] = guid;
							}
						}
					}
				}

				return result;
			}
		}
	}
}
