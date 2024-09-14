using System;
using System.Reflection;
using CommandLine;
using GltfUtility;
using Microsoft.Xna.Framework.Content.Pipeline;

namespace EffectFarm
{
	class Program
	{
		// See system error codes: http://msdn.microsoft.com/en-us/library/windows/desktop/ms681382.aspx
		private const int ERROR_SUCCESS = 0;
		private const int ERROR_BAD_ARGUMENTS = 160;        // 0x0A0
		private const int ERROR_UNHANDLED_EXCEPTION = 574;  // 0x23E

		public static string Version
		{
			get
			{
				var assembly = typeof(Program).Assembly;
				var name = new AssemblyName(assembly.FullName);

				return name.Version.ToString();
			}
		}

		static void Log(string message)
		{
			Console.WriteLine(message);
		}

		static int Process(string[] args)
		{
			Parser.Default.ParseArguments<Options>(args)
				   .WithParsed(o =>
				   {
					   var importer = new OpenAssetImporter();
					   importer.Import(o.InputFile);
				   });

			return ERROR_SUCCESS;
		}

		static int Main(string[] args)
		{
			try
			{
				return Process(args);
			}
			catch (Exception ex)
			{
				Log(ex.ToString());
				return ERROR_UNHANDLED_EXCEPTION;
			}
		}
	}
}