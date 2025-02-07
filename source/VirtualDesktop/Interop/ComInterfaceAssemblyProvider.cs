﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using WindowsDesktop.Properties;

#if NETFRAMEWORK
using Microsoft.CSharp;
using System.CodeDom.Compiler;
#else
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
#endif

namespace WindowsDesktop.Interop
{
	internal class ComInterfaceAssemblyProvider
	{
		private const string _placeholderGuid = "00000000-0000-0000-0000-000000000000";
		private const string _assemblyName = "VirtualDesktop.{0}.generated.dll";

		private static readonly Regex _assemblyRegex = new Regex(@"VirtualDesktop\.(?<build>\d{5}?)(\.\w*|)\.dll");
		private static readonly string _defaultAssemblyDirectoryPath = Path.Combine(ProductInfo.LocalAppData.FullName, "assemblies");
		private static readonly Version _requireVersion = new Version("1.0");
		private static readonly int[] _interfaceVersions = new[] { 10240, 20231, 21313, 21359, 22449, 22621, 25158, 25931, 26100 };

		private readonly string _assemblyDirectoryPath;

		public ComInterfaceAssemblyProvider(string assemblyDirectoryPath)
		{
			this._assemblyDirectoryPath = assemblyDirectoryPath ?? _defaultAssemblyDirectoryPath;
		}

		public Assembly GetAssembly()
		{
			var assembly = this.GetExistingAssembly();
			if (assembly != null) return assembly;

			return this.CreateAssembly();
		}

		public bool TryDeleteAssembly()
		{
			var dir = new DirectoryInfo(this._assemblyDirectoryPath);

			if (!dir.Exists) return false;

			var path = Path.Combine(dir.FullName, string.Format(_assemblyName, ProductInfo.OSBuild));

			if (!File.Exists(path)) return false;

			return this.TryDeleteAssembly(path);
		}

		private Assembly GetExistingAssembly()
		{
			var searchTargets = new[]
			{
				this._assemblyDirectoryPath,
				Environment.CurrentDirectory,
				Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
				_defaultAssemblyDirectoryPath,
			};

			foreach (var searchPath in searchTargets)
			{
				var dir = new DirectoryInfo(searchPath);
				if (!dir.Exists) continue;

				foreach (var file in dir.GetFiles())
				{
					if (int.TryParse(_assemblyRegex.Match(file.Name).Groups["build"]?.ToString(), out var build)
						&& build == ProductInfo.OSBuild)
					{
						try
						{
							var name = AssemblyName.GetAssemblyName(file.FullName);
							if (name.Version >= _requireVersion)
							{
								System.Diagnostics.Debug.WriteLine($"Assembly found: {file.FullName}");
#if !DEBUG
								return Assembly.Load(File.ReadAllBytes(file.FullName));
#endif
							}
						}
						catch (Exception ex)
						{
							System.Diagnostics.Debug.WriteLine("Failed to load assembly: ");
							System.Diagnostics.Debug.WriteLine(ex);

							this.TryDeleteAssembly(file.FullName);
						}
					}
				}
			}

			return null;
		}

		private Assembly CreateAssembly()
		{
			var executingAssembly = Assembly.GetExecutingAssembly();
			var interfaceVersion = ProductInfo.OSBuild == 22621 && ProductInfo.OSRevision < 2215
				? _interfaceVersions
					.Where(build => build != 22621)
					.Reverse()
					.First(build => build <= ProductInfo.OSBuild)
				: _interfaceVersions
					.Reverse()
					.First(build => build <= ProductInfo.OSBuild);
			var interfaceNames = executingAssembly
				.GetTypes()
				.SelectMany(x => x.GetComInterfaceNamesIfWrapper(interfaceVersion))
				.Where(x => x != null)
				.ToArray();
			var iids = IID.GetIIDs(interfaceNames);
			var compileTargets = new List<string>();
			{
				var assemblyInfo = executingAssembly.GetManifestResourceNames().Single(x => x.Contains("AssemblyInfo"));
				var stream = executingAssembly.GetManifestResourceStream(assemblyInfo);
				if (stream != null)
				{
					using (var reader = new StreamReader(stream, Encoding.UTF8))
					{
						var sourceCode = reader.ReadToEnd()
							.Replace("{VERSION}", ProductInfo.OSBuild.ToString())
							.Replace("{BUILD}", interfaceVersion.ToString());
						compileTargets.Add(sourceCode);
					}
				}
			}

			foreach (var name in executingAssembly.GetManifestResourceNames())
			{
				var texts = Path.GetFileNameWithoutExtension(name)?.Split('.');
				var typeName = texts.LastOrDefault();
				if (typeName == null) continue;

				if (int.TryParse(string.Concat(texts[texts.Length - 2].Skip(1)), out var build) && build != interfaceVersion) continue;

				var interfaceName = interfaceNames.FirstOrDefault(x => typeName == x);
				if (interfaceName == null) continue;
				if (!iids.ContainsKey(interfaceName)) continue;

				var stream = executingAssembly.GetManifestResourceStream(name);
				if (stream == null) continue;

				using (var reader = new StreamReader(stream, Encoding.UTF8))
				{
					var sourceCode = reader.ReadToEnd().Replace(_placeholderGuid, iids[interfaceName].ToString());
					compileTargets.Add(sourceCode);
				}
			}

			return this.Compile(compileTargets.ToArray());
		}

		private Assembly Compile(IEnumerable<string> sources)
		{
			try
			{
				var dir = new DirectoryInfo(this._assemblyDirectoryPath);

				if (!dir.Exists) dir.Create();

#if NETFRAMEWORK
				using (var provider = new CSharpCodeProvider())
				{
					var path = Path.Combine(dir.FullName, string.Format(_assemblyName, ProductInfo.OSBuild));
					var cp = new CompilerParameters
					{
						OutputAssembly = path,
						GenerateExecutable = false,
						GenerateInMemory = false,
					};
					cp.ReferencedAssemblies.Add("System.dll");
					cp.ReferencedAssemblies.Add(Assembly.GetExecutingAssembly().Location);

					var result = provider.CompileAssemblyFromSource(cp, sources.ToArray());
					if (result.Errors.Count > 0) 
					{
						this.TryDeleteAssembly(path);

						var nl = Environment.NewLine;
						var message = $"Failed to compile COM interfaces assembly.{nl}{string.Join(nl, result.Errors.OfType<CompilerError>().Select(x => $"  {x}"))}";

						throw new Exception(message);
					}

					System.Diagnostics.Debug.WriteLine($"Assembly compiled: {path}");
					return result.CompiledAssembly;
				}
#else
				var path = Path.Combine(dir.FullName, string.Format(_assemblyName, ProductInfo.OSBuild));
				var syntaxTrees = sources.Select(x => SyntaxFactory.ParseSyntaxTree(x));
				var references = AppDomain.CurrentDomain.GetAssemblies()
					.Concat(new[] { Assembly.GetExecutingAssembly(), })
					.Select(x => x.Location)
					.Select(x => MetadataReference.CreateFromFile(x));
				var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
				var compilation = CSharpCompilation.Create(_assemblyName)
					.WithOptions(options)
					.WithReferences(references)
					.AddSyntaxTrees(syntaxTrees);

				var result = compilation.Emit(path);
				if (result.Success)
				{
					return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
				}

				this.TryDeleteAssembly(path);

				var nl = Environment.NewLine;
				var message = $"Failed to compile COM interfaces assembly.{nl}{string.Join(nl, result.Diagnostics.Select(x => $"  {x.GetMessage()}"))}";

				throw new Exception(message);
#endif
			}
			finally
			{
				GC.Collect();
			}
		}

		private bool TryDeleteAssembly(string path)
		{
			try
			{
				File.Delete(path);
				return true;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine("Failed to delete assembly: ");
				System.Diagnostics.Debug.WriteLine(ex);
				return false;
			}
		}
	}
}
