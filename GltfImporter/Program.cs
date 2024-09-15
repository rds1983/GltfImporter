using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using CommandLine;
using GltfImporter;
using glTFLoader.Schema;
using glTFLoader;
using GltfUtility;
using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Content.Pipeline.Graphics;
using Microsoft.Xna.Framework.Graphics;
using MonoGame.Tests.ContentPipeline;
using Microsoft.Xna.Framework;

namespace EffectFarm
{
	class Program
	{
		// See system error codes: http://msdn.microsoft.com/en-us/library/windows/desktop/ms681382.aspx
		private const int ERROR_SUCCESS = 0;
		private const int ERROR_UNHANDLED_EXCEPTION = 574;  // 0x23E

		static void RecursiveAction(NodeContent root, Action<NodeContent> action)
		{
			action(root);

			foreach (var child in root.Children)
			{
				RecursiveAction(child, action);
			}
		}

		static unsafe void GenerateGlb(NodeContent root, Options o)
		{
			// Gather nodes & meshes
			var sourceNodes = new List<NodeContent>();
			var sourceMeshes = new List<MeshContent>();

			RecursiveAction(root, n =>
			{
				sourceNodes.Add(n);

				var asMesh = n as MeshContent;
				if (asMesh != null)
				{
					sourceMeshes.Add(asMesh);
				}
			});

			var gltfMeshes = new List<Mesh>();
			var bufferViews = new List<BufferView>();
			var accessors = new List<Accessor>();
			var nodesMeshes = new Dictionary<string, int>();

			var totalVertices = 0;
			byte[] buffer;
			using (var ms = new MemoryStream())
			{
				foreach (var mesh in sourceMeshes)
				{
					var primitives = new List<MeshPrimitive>();
					foreach (var part in mesh.Geometry)
					{
						var primitive = new MeshPrimitive
						{
							Attributes = new Dictionary<string, int>()
						};

						var vertexBuffer = part.Vertices.CreateVertexBuffer();
						totalVertices += part.Vertices.VertexCount;

						var data = vertexBuffer.VertexData;

						var partOffset = 0;

						var elements = vertexBuffer.VertexDeclaration.VertexElements;
						for (var i = 0; i < elements.Count; ++i)
						{
							var element = elements[i];

							int? accessor = null;
							fixed (byte* cptr = &data[partOffset + element.Offset])
							{
								var ptr = cptr;

								switch (element.VertexElementFormat)
								{
									case VertexElementFormat.Vector2:
										var v2 = new List<Vector2>();
										for (var j = 0; j < part.Vertices.VertexCount; ++j)
										{
											v2.Add(*(Vector2*)ptr);
											ptr += vertexBuffer.VertexDeclaration.VertexStride.Value;

										}
										accessor = ms.WriteData(bufferViews, accessors, v2.ToArray());
										break;
									case VertexElementFormat.Vector3:
										var v3 = new List<Vector3>();
										for (var j = 0; j < part.Vertices.VertexCount; ++j)
										{
											v3.Add(*(Vector3*)ptr);
											ptr += vertexBuffer.VertexDeclaration.VertexStride.Value;

										}
										accessor = ms.WriteData(bufferViews, accessors, v3.ToArray());
										break;
									case VertexElementFormat.Vector4:
										var v4 = new List<Vector4>();
										for (var j = 0; j < part.Vertices.VertexCount; ++j)
										{
											v4.Add(*(Vector4*)ptr);
											ptr += vertexBuffer.VertexDeclaration.VertexStride.Value;

										}
										accessor = ms.WriteData(bufferViews, accessors, v4.ToArray());
										break;
									case VertexElementFormat.Color:
										var cc = new List<Vector4>();
										for (var j = 0; j < part.Vertices.VertexCount; ++j)
										{
											var v = *(Color*)ptr;
											cc.Add(v.ToVector4());
											ptr += vertexBuffer.VertexDeclaration.VertexStride.Value;

										}
										accessor = ms.WriteData(bufferViews, accessors, cc.ToArray());
										break;

									default:
										throw new Exception($"Can't process {element.VertexElementFormat}");
								}

							}

							switch (element.VertexElementUsage)
							{
								case VertexElementUsage.Position:
									primitive.Attributes["POSITION"] = accessor.Value;
									break;
								case VertexElementUsage.Color:
									primitive.Attributes["COLOR_" + element.UsageIndex] = accessor.Value;
									break;
								case VertexElementUsage.TextureCoordinate:
									primitive.Attributes["TEXCOORD_" + element.UsageIndex] = accessor.Value;
									break;
								case VertexElementUsage.Tangent:
									primitive.Attributes["TANGENT"] = accessor.Value;
									break;
								case VertexElementUsage.Binormal:
									primitive.Attributes["BINORMAL"] = accessor.Value;
									break;
								case VertexElementUsage.Normal:
									primitive.Attributes["NORMAL"] = accessor.Value;
									break;

								default:
									throw new Exception($"Can't process {element.VertexElementUsage}");
							}
						}

						var indices = part.Indices;

						// Convert to short
						var indicesShort = new ushort[indices.Count];

						for (var i = 0; i < indices.Count; ++i)
						{
							if (indices[i] > ushort.MaxValue)
							{
								throw new Exception("Index out of range");
							}

							indicesShort[i] = (ushort)indices[i];
						}

						// Unwind
						for (var i = 0; i < indicesShort.Length; i += 3)
						{
							var temp = indicesShort[i];
							indicesShort[i] = indicesShort[i + 2];
							indicesShort[i + 2] = temp;
						}

						primitive.Indices = ms.WriteData(bufferViews, accessors, indicesShort.ToArray());

						primitives.Add(primitive);
					}

					var gltfMesh = new Mesh
					{
						Name = mesh.Name,
						Primitives = primitives.ToArray()
					};

					nodesMeshes[mesh.Name] = gltfMeshes.Count;

					gltfMeshes.Add(gltfMesh);
				}

				buffer = ms.ToArray();
			}

			var nodes = new List<Node>();
			foreach (var bone in sourceNodes)
			{
				var gltfNode = new Node
				{
					Name = bone.Name,
					Matrix = bone.Transform.ToFloats(),
				};

				nodes.Add(gltfNode);
			}

			// Set children
			foreach (var bone in sourceNodes)
			{
				var node = (from n in nodes where n.Name == bone.Name select n).First();

				var children = new List<int>();
				foreach (var child in bone.Children)
				{
					int? index = null;
					for (var i = 0; i < nodes.Count; ++i)
					{
						if (child.Name == nodes[i].Name)
						{
							index = i;
							break;
						}
					}

					if (index == null)
					{
						throw new Exception($"Could not find node {child.Name}");
					}

					children.Add(index.Value);
				}

				if (children.Count > 0)
				{
					node.Children = children.ToArray();
				}
			}

			// Set nodes meshes
			foreach (var pair in nodesMeshes)
			{
				var node = (from n in nodes where n.Name == pair.Key select n).First();

				node.Mesh = pair.Value;
			}

			var buf = new glTFLoader.Schema.Buffer
			{
				ByteLength = buffer.Length
			};

			var scene = new Scene
			{
				Nodes = [0]
			};

			var gltf = new Gltf
			{
				Asset = new Asset
				{
					Generator = "xnb2gltf",
					Version = "2.0",
				},
				Buffers = [buf],
				BufferViews = bufferViews.ToArray(),
				Accessors = accessors.ToArray(),
				Nodes = nodes.ToArray(),
				Meshes = gltfMeshes.ToArray(),
				Scenes = [scene],
				Scene = 0
			};

			var output = Path.ChangeExtension(o.InputFile, "glb");

			Debug.WriteLine(output);
			Interface.SaveBinaryModel(gltf, buffer, output);
		}

		static void Run(Options o)
		{
			var context = new TestImporterContext(".", ".");
			var importer = new OpenAssetImporter();
			var root = importer.Import(o.InputFile, context);

			GenerateGlb(root, o);
		}

		static int Process(string[] args)
		{
			Parser.Default.ParseArguments<Options>(args)
				   .WithParsed(o => Run(o));

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
				Logger.LogError(ex.ToString());
				return ERROR_UNHANDLED_EXCEPTION;
			}
		}
	}
}