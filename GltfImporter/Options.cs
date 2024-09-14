using CommandLine;

namespace GltfUtility
{
	public class Options
	{
		[Option('i', "input", Required = true, HelpText = "The input/output gltf/glb(version 2) file.")]
		public string InputFile { get; set; }

		[Option('o', "output", Required = false, HelpText = "Name of the output file.")]
		public string OutputFile { get; set; }

		[Option('t', "tangent", Required = false, HelpText = "Determines whether to generate tangent frames.")]
		public bool Tangent { get; set; }

		[Option('u', "unwind", Required = false, HelpText = "Determines whether to unwind indices.")]
		public bool Unwind { get; set; }

		[Option('p', "premultiply", Required = false, HelpText = "Determines whether to premultiply vertex colors.")]
		public bool Premultiply { get; set; }

		[Option('s', "scale", Required = false, HelpText = "Defines the scale that should be applied to the model.")]
		public float? Scale { get; set; }
	}
}
