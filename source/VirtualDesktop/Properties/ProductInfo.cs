﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace WindowsDesktop.Properties
{
	public class ProductInfo
	{
		private static readonly Assembly _assembly = Assembly.GetExecutingAssembly();
		private static readonly Lazy<string> _titleLazy = new Lazy<string>(() => ((AssemblyTitleAttribute)Attribute.GetCustomAttribute(_assembly, typeof(AssemblyTitleAttribute))).Title);
		private static readonly Lazy<string> _descriptionLazy = new Lazy<string>(() => ((AssemblyDescriptionAttribute)Attribute.GetCustomAttribute(_assembly, typeof(AssemblyDescriptionAttribute))).Description);
		private static readonly Lazy<string> _companyLazy = new Lazy<string>(() => ((AssemblyCompanyAttribute)Attribute.GetCustomAttribute(_assembly, typeof(AssemblyCompanyAttribute))).Company);
		private static readonly Lazy<string> _productLazy = new Lazy<string>(() => ((AssemblyProductAttribute)Attribute.GetCustomAttribute(_assembly, typeof(AssemblyProductAttribute))).Product);
		private static readonly Lazy<string> _copyrightLazy = new Lazy<string>(() => ((AssemblyCopyrightAttribute)Attribute.GetCustomAttribute(_assembly, typeof(AssemblyCopyrightAttribute))).Copyright);
		private static readonly Lazy<string> _trademarkLazy = new Lazy<string>(() => ((AssemblyTrademarkAttribute)Attribute.GetCustomAttribute(_assembly, typeof(AssemblyTrademarkAttribute))).Trademark);
		private static readonly Lazy<string> _versionLazy = new Lazy<string>(() => $"{Version.ToString(3)}");
		private static readonly Lazy<DirectoryInfo> _localAppDataLazy = new Lazy<DirectoryInfo>(() => new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Company, Product)));
		private static readonly Lazy<int> _osRevision = new Lazy<int>(() =>
		{
			var registryKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
			var ubr = registryKey.GetValue("UBR").ToString();
			if (int.TryParse(ubr, out var revision))
			{
				return revision;
			}
			return 0;
		});

		public static string Title => _titleLazy.Value;

		public static string Description => _descriptionLazy.Value;

		public static string Company => _companyLazy.Value;

		public static string Product => _productLazy.Value;

		public static string Copyright => _copyrightLazy.Value;

		public static string Trademark => _trademarkLazy.Value;

		public static Version Version => _assembly.GetName().Version;

		public static string VersionString => _versionLazy.Value;

		internal static DirectoryInfo LocalAppData => _localAppDataLazy.Value;

		// Once windows 11 comes out this will probably report the latest build that is Windows 10 (or Windows 11 Preview)
		// until the app.manifest has the Windows 11 sSupportedOS ID
		internal static int OSBuild => Environment.OSVersion.Version.Build;
		internal static int OSRevision => _osRevision.Value;
	}
}
