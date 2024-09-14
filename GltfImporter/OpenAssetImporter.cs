﻿// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using Assimp;
using GltfImporter;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Quaternion = System.Numerics.Quaternion;

namespace Microsoft.Xna.Framework.Content.Pipeline
{
	public class OpenAssetImporter
	{
		// Assimp has a few limitations (not all FBX files are supported):
		// FBX files reference objects using IDs. Therefore, it is possible to resolve
		// bones even if multiple bones/nodes have the same name. But Assimp references
		// bones only by name!
		// --> Limitation #1: A model cannot have more than one skeleton!
		// --> Limitation #2: Bone names need to be unique!
		//
		// Bones are represented by regular nodes, but there is no flag indicating whether
		// a node is a bone. A mesh in Assimp references deformation bones (= bones that
		// affect vertices) by name. That means, we can identify the nodes that represent
		// deformation bones. But there is no way to identify helper bones (= bones that 
		// belong to the skeleton, but do not affect vertices). As described in 
		// http://assimp.sourceforge.net/lib_html/data.html and 
		// http://gamedev.stackexchange.com/questions/26382/i-cant-figure-out-how-to-animate-my-loaded-model-with-assimp/26442#26442
		// we can only guess which nodes belong to a skeleton:
		// --> Limitation #3: The skeleton needs to be a direct child of the root node or
		//                    the mesh node!
		//
		// Node.Transform is irrelevant for bones. This transform is just the pose of the
		// bone at the time of the export. This could be one of the animation frames. It
		// is not necessarily the bind pose (rest pose)! For example, XNA's Dude.fbx does
		// NOT store the skeleton in bind pose.
		// The correct transform is stored in Mesh.Bones[i].OffsetMatrix4x4. However, this
		// information is only available for deformation bones, not for helper bones.
		// --> Limitation #4: The skeleton either must not contain helper bones, or it must
		//                    be guaranteed that the skeleton is exported in bind pose!
		//
		// An FBX file does not directly store all animation values. In some FBX scene it
		// is insufficient to simply read the animation data from the file. Instead, the
		// animation properties of all relevant objects in the scene need to be evaluated.
		// For example, some animations are the result of the current skeleton pose + the
		// current animation. The current skeleton pose is not imported/processed by XNA.
		// Assimp does not include an "animation evaluater" that automatically bakes these
		// animations.
		// --> Limitation #5: All bones included in an animation need to be key framed.
		//                    (There is no automatic evaluation.)
		//
		// In FBX it is possible to define animations curves for some transform components
		// (e.g. translation X and Y) and leave other components (e.g. translation Z) undefined.
		// Assimp does not pick the right defaults for undefined components.
		// --> Limitation #6: When scale, rotation, or translation is animated, all components
		//                    X, Y, Z need to be key framed.

		#region Nested Types
		/// <summary>Defines the frame for local scale/rotation/translation of FBX nodes.</summary>
		/// <remarks>
		/// <para>
		/// The transformation pivot defines the frame for local scale/rotation/translation. The
		/// local transform of a node is:
		/// </para>
		/// <para>
		/// Local Transform = Translation * RotationOffset * RotationPivot * PreRotation
		///                   * Rotation * PostRotation * RotationPivotInverse * ScalingOffset
		///                   * ScalingPivot * Scaling * ScalingPivotInverse
		/// </para>
		/// <para>
		/// where the Matrix4x4 multiplication order is right-to-left.
		/// </para>
		/// <para>
		/// 3ds max uses three additional transformations:
		/// </para>
		/// <para>
		/// Local Transform = Translation * Rotation * Scaling
		///                   * GeometricTranslation * GeometricRotation * GeometricScaling
		/// </para>
		/// <para>
		/// Transformation pivots are stored per FBX node. When Assimp hits an FBX node with
		/// a transformation pivot it generates additional nodes named
		/// </para>
		/// <para>
		///   <i>OriginalName</i>_$AssimpFbx$_<i>TransformName</i>
		/// </para>
		/// <para>
		/// where <i>TransformName</i> is one of: 
		/// </para>
		/// <para>
		///   Translation, RotationOffset, RotationPivot, PreRotation, Rotation, PostRotation,
		///   RotationPivotInverse, ScalingOffset, ScalingPivot, Scaling, ScalingPivotInverse,
		///   GeometricTranslation, GeometricRotation, GeometricScaling
		/// </para>
		/// </remarks>
		/// <seealso href="http://download.autodesk.com/us/fbx/20112/FBX_SDK_HELP/index.html?url=WS1a9193826455f5ff1f92379812724681e696651.htm,topicNumber=d0e7429"/>
		/// <seealso href="http://area.autodesk.com/forum/autodesk-fbx/fbx-sdk/the-makeup-of-the-local-Matrix4x4-of-an-kfbxnode/"/>
		private class FbxPivot
		{
			public static readonly FbxPivot Default = new FbxPivot();

			public Matrix4x4? Translation;
			public Matrix4x4? RotationOffset;
			public Matrix4x4? RotationPivot;
			public Matrix4x4? PreRotation;
			public Matrix4x4? Rotation;
			public Matrix4x4? PostRotation;
			public Matrix4x4? RotationPivotInverse;
			public Matrix4x4? ScalingOffset;
			public Matrix4x4? ScalingPivot;
			public Matrix4x4? Scaling;
			public Matrix4x4? ScalingPivotInverse;
			public Matrix4x4? GeometricTranslation;
			public Matrix4x4? GeometricRotation;
			public Matrix4x4? GeometricScaling;

			public Matrix4x4 GetTransform(Vector3? scale, Quaternion? rotation, Vector3? translation)
			{
				var transform = Matrix4x4.Identity;

				if (GeometricScaling.HasValue)
					transform *= GeometricScaling.Value;
				if (GeometricRotation.HasValue)
					transform *= GeometricRotation.Value;
				if (GeometricTranslation.HasValue)
					transform *= GeometricTranslation.Value;
				if (ScalingPivotInverse.HasValue)
					transform *= ScalingPivotInverse.Value;
				if (scale.HasValue)
					transform *= Matrix4x4.CreateScale(scale.Value);
				else if (Scaling.HasValue)
					transform *= Scaling.Value;
				if (ScalingPivot.HasValue)
					transform *= ScalingPivot.Value;
				if (ScalingOffset.HasValue)
					transform *= ScalingOffset.Value;
				if (RotationPivotInverse.HasValue)
					transform *= RotationPivotInverse.Value;
				if (PostRotation.HasValue)
					transform *= PostRotation.Value;
				if (rotation.HasValue)
					transform *= Matrix4x4.CreateFromQuaternion(rotation.Value);
				else if (Rotation.HasValue)
					transform *= Rotation.Value;
				if (PreRotation.HasValue)
					transform *= PreRotation.Value;
				if (RotationPivot.HasValue)
					transform *= RotationPivot.Value;
				if (RotationOffset.HasValue)
					transform *= RotationOffset.Value;
				if (translation.HasValue)
					transform *= Matrix4x4.CreateTranslation(translation.Value);
				else if (Translation.HasValue)
					transform *= Translation.Value;

				return transform;
			}
		}
		#endregion

		private static readonly List<VectorKey> EmptyVectorKeys = new List<VectorKey>();
		private static readonly List<QuaternionKey> EmptyQuaternionKeys = new List<QuaternionKey>();

		// Assimp scene
		private Scene _scene;
		private Dictionary<string, Matrix4x4> _deformationBones;   // The names and offset matrices of all deformation bones.
		private Node _rootBone;                                 // The node that represents the root bone.
		private List<Node> _bones = new List<Node>();           // All nodes attached to the root bone.
		private Dictionary<string, FbxPivot> _pivots;              // The transformation pivots.

		// XNA content
		private NodeContent _rootNode;
		private List<MaterialContent> _materials;

		// This is used to enable backwards compatibility with
		// XNA providing a model as expected from the original
		// FbxImporter and XImporter.
		private readonly bool _xnaCompatible;

		/// <summary>
		/// Default constructor.
		/// </summary>
		public OpenAssetImporter(): this(false)
		{
		}

		internal OpenAssetImporter(bool xnaCompatible)
		{
			_xnaCompatible = xnaCompatible;
		}

		/// <summary>
		/// This disables some Assimp model loading features so that
		/// the resulting content is the same as what the XNA FbxImporter 
		/// </summary>
		public bool XnaComptatible { get; set; }

		public NodeContent Import(string filename)
		{
			if (filename == null)
				throw new ArgumentNullException("filename");

			using (var importer = new AssimpContext())
			{
				// FBXPreservePivotsConfig(false) can be set to remove transformation
				// pivots. However, Assimp does not automatically correct animations!
				// --> Leave default settings, handle transformation pivots explicitly.
				//importer.SetConfig(new Assimp.Configs.FBXPreservePivotsConfig(false));

				// Set flag to remove degenerate faces (points and lines).
				// This flag is very important when PostProcessSteps.FindDegenerates is used
				// because FindDegenerates converts degenerate triangles to points and lines!
				importer.SetConfig(new Assimp.Configs.RemoveDegeneratePrimitivesConfig(true));

				// Note about Assimp post-processing:
				// Keep post-processing to a minimum. The ModelImporter should import
				// the model as is. We don't want to lose any information, i.e. empty
				// nodes shoud not be thrown away, meshes/materials should not be merged,
				// etc. Custom model processors may depend on this information!
				_scene = importer.ImportFile(filename,
					PostProcessSteps.FindDegenerates |
					PostProcessSteps.FindInvalidData |
					PostProcessSteps.FlipUVs |              // Required for Direct3D
					PostProcessSteps.FlipWindingOrder |     // Required for Direct3D
					PostProcessSteps.JoinIdenticalVertices |
					PostProcessSteps.ImproveCacheLocality |
					PostProcessSteps.OptimizeMeshes |
					PostProcessSteps.Triangulate

					// Unused: 
					//PostProcessSteps.CalculateTangentSpace
					//PostProcessSteps.Debone |
					//PostProcessSteps.FindInstances |      // No effect + slow?
					//PostProcessSteps.FixInFacingNormals |
					//PostProcessSteps.GenerateNormals |
					//PostProcessSteps.GenerateSmoothNormals |
					//PostProcessSteps.GenerateUVCoords | // Might be needed... find test case
					//PostProcessSteps.LimitBoneWeights |
					//PostProcessSteps.MakeLeftHanded |     // Not necessary, XNA is right-handed.
					//PostProcessSteps.OptimizeGraph |      // Will eliminate helper nodes
					//PostProcessSteps.PreTransformVertices |
					//PostProcessSteps.RemoveComponent |
					//PostProcessSteps.RemoveRedundantMaterials |
					//PostProcessSteps.SortByPrimitiveType |
					//PostProcessSteps.SplitByBoneCount |
					//PostProcessSteps.SplitLargeMeshes |
					//PostProcessSteps.TransformUVCoords |
					//PostProcessSteps.ValidateDataStructure |
					);

				FindSkeleton();     // Find _rootBone, _bones, _deformationBones.

				// Create _materials.
				if (_xnaCompatible)
					ImportXnaMaterials();
				else
					ImportMaterials();

				ImportNodes();      // Create _pivots and _rootNode (incl. children).
				ImportSkeleton();   // Create skeleton (incl. animations) and add to _rootNode.

				// If we have a simple hierarchy with no bones and just the one
				// mesh, we can flatten it out so the mesh is the root node.
				if (_rootNode.Children.Count == 1 && _rootNode.Children[0] is MeshContent)
				{
					var absXform = _rootNode.Children[0].AbsoluteTransform;
					_rootNode = _rootNode.Children[0];
					_rootNode.Transform = absXform;
				}

				_scene.Clear();
			}

			return _rootNode;
		}

		/// <summary>
		/// Converts all Assimp <see cref="Material"/>s to standard XNA compatible <see cref="MaterialContent"/>s.
		/// </summary>
		private void ImportXnaMaterials()
		{
			_materials = new List<MaterialContent>();
			foreach (var aiMaterial in _scene.Materials)
			{
				// TODO: What about AlphaTestMaterialContent, DualTextureMaterialContent, 
				// EffectMaterialContent, EnvironmentMapMaterialContent, and SkinnedMaterialContent?

				var material = new BasicMaterialContent
				{
					Name = aiMaterial.Name,
				};

				if (aiMaterial.HasTextureDiffuse)
					material.Texture = ImportTextureContentRef(aiMaterial.TextureDiffuse);

				if (aiMaterial.HasTextureOpacity)
					material.Textures.Add("Transparency", ImportTextureContentRef(aiMaterial.TextureOpacity));

				if (aiMaterial.HasTextureSpecular)
					material.Textures.Add("Specular", ImportTextureContentRef(aiMaterial.TextureSpecular));

				if (aiMaterial.HasTextureHeight)
					material.Textures.Add("Bump", ImportTextureContentRef(aiMaterial.TextureHeight));

				if (aiMaterial.HasColorDiffuse)
					material.DiffuseColor = ToXna(aiMaterial.ColorDiffuse);

				if (aiMaterial.HasColorEmissive)
					material.EmissiveColor = ToXna(aiMaterial.ColorEmissive);

				if (aiMaterial.HasOpacity)
					material.Alpha = aiMaterial.Opacity;

				if (aiMaterial.HasColorSpecular)
					material.SpecularColor = ToXna(aiMaterial.ColorSpecular);

				if (aiMaterial.HasShininessStrength)
					material.SpecularPower = aiMaterial.Shininess;

				_materials.Add(material);
			}
		}

		private TextureContent ImportTextureContentRef(TextureSlot textureSlot)
		{
			var texture = new TextureContent(textureSlot.FilePath);
			texture.OpaqueData.Add("TextureCoordinate", string.Format("TextureCoordinate{0}", textureSlot.UVIndex));

			if (!_xnaCompatible)
			{
				texture.OpaqueData.Add("Operation", textureSlot.Operation.ToString());
				texture.OpaqueData.Add("AddressU", textureSlot.WrapModeU.ToString());
				texture.OpaqueData.Add("AddressV", textureSlot.WrapModeU.ToString());
				texture.OpaqueData.Add("Mapping", textureSlot.Mapping.ToString());
			}

			return texture;
		}

		/// <summary>
		/// Returns all the Assimp <see cref="Material"/> features as a <see cref="MaterialContent"/>.
		/// </summary>
		private void ImportMaterials()
		{
			_materials = new List<MaterialContent>();
			foreach (var aiMaterial in _scene.Materials)
			{
				// TODO: Should we create a special AssImpMaterial?

				var material = new MaterialContent
				{
					Name = aiMaterial.Name,
				};

				var slots = aiMaterial.GetAllMaterialTextures();
				foreach (var tex in slots)
				{
					string name;

					// Force the XNA naming standard for diffuse textures
					// which allows the material to work with the stock
					// model processor.
					if (tex.TextureType == TextureType.Diffuse)
						name = BasicMaterialContent.TextureKey;
					else
						name = tex.TextureType.ToString();

					// We might have multiple textures of the same type so number
					// them starting with 2 like in DualTextureMaterialContent.
					if (tex.TextureIndex > 0)
						name += (tex.TextureIndex + 1);

					material.Textures.Add(name, ImportTextureContentRef(tex));
				}

				if (aiMaterial.HasBlendMode)
					material.OpaqueData.Add("BlendMode", aiMaterial.BlendMode.ToString());
				if (aiMaterial.HasBumpScaling)
					material.OpaqueData.Add("BumpScaling", aiMaterial.BumpScaling);
				if (aiMaterial.HasColorAmbient)
					material.OpaqueData.Add("AmbientColor", ToXna(aiMaterial.ColorAmbient));
				if (aiMaterial.HasColorDiffuse)
					material.OpaqueData.Add("DiffuseColor", ToXna(aiMaterial.ColorDiffuse));
				if (aiMaterial.HasColorEmissive)
					material.OpaqueData.Add("EmissiveColor", ToXna(aiMaterial.ColorEmissive));
				if (aiMaterial.HasColorReflective)
					material.OpaqueData.Add("ReflectiveColor", ToXna(aiMaterial.ColorReflective));
				if (aiMaterial.HasColorSpecular)
					material.OpaqueData.Add("SpecularColor", ToXna(aiMaterial.ColorSpecular));
				if (aiMaterial.HasColorTransparent)
					material.OpaqueData.Add("TransparentColor", ToXna(aiMaterial.ColorTransparent));
				if (aiMaterial.HasOpacity)
					material.OpaqueData.Add("Opacity", aiMaterial.Opacity);
				if (aiMaterial.HasReflectivity)
					material.OpaqueData.Add("Reflectivity", aiMaterial.Reflectivity);
				if (aiMaterial.HasShadingMode)
					material.OpaqueData.Add("ShadingMode", aiMaterial.ShadingMode.ToString());
				if (aiMaterial.HasShininess)
					material.OpaqueData.Add("Shininess", aiMaterial.Shininess);
				if (aiMaterial.HasShininessStrength)
					material.OpaqueData.Add("ShininessStrength", aiMaterial.ShininessStrength);
				if (aiMaterial.HasTwoSided)
					material.OpaqueData.Add("TwoSided", aiMaterial.IsTwoSided);
				if (aiMaterial.HasWireFrame)
					material.OpaqueData.Add("WireFrame", aiMaterial.IsWireFrameEnabled);

				_materials.Add(material);
			}
		}

		/// <summary>
		/// Converts all Assimp nodes to XNA nodes. (Nodes representing bones are excluded!)
		/// </summary>
		private void ImportNodes()
		{
			_pivots = new Dictionary<string, FbxPivot>();
			_rootNode = ImportNodes(_scene.RootNode, null, null);
		}

		/// <summary>
		/// Converts the specified node and all descendant nodes.
		/// </summary>
		/// <param name="aiNode">The node.</param>
		/// <param name="aiParent">The parent node. Can be <see langword="null"/>.</param>
		/// <param name="parent">The <paramref name="aiParent"/> node converted to XNA.</param>
		/// <returns>The XNA <see cref="NodeContent"/>.</returns>
		/// <remarks>
		/// It may be necessary to skip certain "preserve pivot" nodes in the hierarchy. The
		/// converted node needs to be relative to <paramref name="aiParent"/>, not <c>node.Parent</c>.
		/// </remarks>
		private NodeContent ImportNodes(Node aiNode, Node aiParent, NodeContent parent)
		{
			Debug.Assert(aiNode != null);

			NodeContent node = null;
			if (aiNode.HasMeshes)
			{
				var mesh = new MeshContent
				{
					Name = aiNode.Name,
					Transform = ToXna(GetRelativeTransform(aiNode, aiParent))
				};

				foreach (var meshIndex in aiNode.MeshIndices)
				{
					var aiMesh = _scene.Meshes[meshIndex];
					if (!aiMesh.HasVertices)
						continue;

					var geom = CreateGeometry(mesh, aiMesh);
					mesh.Geometry.Add(geom);
				}

				node = mesh;
			}
			else if (aiNode.Name.Contains("_$AssimpFbx$"))
			{
				// This is a transformation pivot.
				//   <OriginalName>_$AssimpFbx$_<TransformName>
				// where <TransformName> is one of
				//   Translation, RotationOffset, RotationPivot, PreRotation, Rotation,
				//   PostRotation, RotationPivotInverse, ScalingOffset, ScalingPivot,
				//   Scaling, ScalingPivotInverse
				string originalName = GetNodeName(aiNode.Name);
				FbxPivot pivot;
				if (!_pivots.TryGetValue(originalName, out pivot))
				{
					pivot = new FbxPivot();
					_pivots.Add(originalName, pivot);
				}

				Matrix4x4 transform = ToXna(aiNode.Transform);
				if (aiNode.Name.EndsWith("_Translation"))
					pivot.Translation = transform;
				else if (aiNode.Name.EndsWith("_RotationOffset"))
					pivot.RotationOffset = transform;
				else if (aiNode.Name.EndsWith("_RotationPivot"))
					pivot.RotationPivot = transform;
				else if (aiNode.Name.EndsWith("_PreRotation"))
					pivot.PreRotation = transform;
				else if (aiNode.Name.EndsWith("_Rotation"))
					pivot.Rotation = transform;
				else if (aiNode.Name.EndsWith("_PostRotation"))
					pivot.PostRotation = transform;
				else if (aiNode.Name.EndsWith("_RotationPivotInverse"))
					pivot.RotationPivotInverse = transform;
				else if (aiNode.Name.EndsWith("_ScalingOffset"))
					pivot.ScalingOffset = transform;
				else if (aiNode.Name.EndsWith("_ScalingPivot"))
					pivot.ScalingPivot = transform;
				else if (aiNode.Name.EndsWith("_Scaling"))
					pivot.Scaling = transform;
				else if (aiNode.Name.EndsWith("_ScalingPivotInverse"))
					pivot.ScalingPivotInverse = transform;
				else if (aiNode.Name.EndsWith("_GeometricTranslation"))
					pivot.GeometricTranslation = transform;
				else if (aiNode.Name.EndsWith("_GeometricRotation"))
					pivot.GeometricRotation = transform;
				else if (aiNode.Name.EndsWith("_GeometricScaling"))
					pivot.GeometricScaling = transform;
				else
					throw new Exception(string.Format("Unknown $AssimpFbx$ node: \"{0}\"", aiNode.Name));
			}
			else if (!_bones.Contains(aiNode)) // Ignore bones.
			{
				node = new NodeContent
				{
					Name = aiNode.Name,
					Transform = ToXna(GetRelativeTransform(aiNode, aiParent))
				};
			}

			if (node != null)
			{
				if (parent != null)
					parent.Children.Add(node);

				// For the children, this is the new parent.
				aiParent = aiNode;
				parent = node;

				if (_scene.HasAnimations)
				{
					foreach (var animation in _scene.Animations)
					{
						var animationContent = ImportAnimation(animation, node.Name);
						if (animationContent.Channels.Count > 0)
							node.Animations.Add(animationContent.Name, animationContent);
					}
				}
			}

			Debug.Assert(parent != null);

			foreach (var child in aiNode.Children)
				ImportNodes(child, aiParent, parent);

			return node;
		}

		private GeometryContent CreateGeometry(MeshContent mesh, Mesh aiMesh)
		{
			var geom = new GeometryContent
			{
				Material = _materials[aiMesh.MaterialIndex]
			};

			// Vertices
			var baseVertex = mesh.Positions.Count;
			foreach (var vert in aiMesh.Vertices)
				mesh.Positions.Add(ToXna(vert));
			geom.Vertices.AddRange(Enumerable.Range(baseVertex, aiMesh.VertexCount));
			geom.Indices.AddRange(aiMesh.GetIndices());

			if (aiMesh.HasBones)
			{
				var xnaWeights = new List<BoneWeightCollection>();
				var vertexCount = geom.Vertices.VertexCount;
				bool missingBoneWeights = false;
				for (var i = 0; i < vertexCount; i++)
				{
					var list = new BoneWeightCollection();
					for (var boneIndex = 0; boneIndex < aiMesh.BoneCount; boneIndex++)
					{
						var bone = aiMesh.Bones[boneIndex];
						foreach (var weight in bone.VertexWeights)
						{
							if (weight.VertexID != i)
								continue;

							list.Add(new BoneWeight(bone.Name, weight.Weight));
						}
					}

					if (list.Count == 0)
					{
						// No bone weights found for vertex. Use bone 0 as fallback.
						missingBoneWeights = true;
						list.Add(new BoneWeight(aiMesh.Bones[0].Name, 1));
					}

					xnaWeights.Add(list);
				}

				if (missingBoneWeights)
				{
					Logger.LogWarning($"No bone weights found for one or more vertices of skinned mesh '{aiMesh.Name}'.");
				}

				geom.Vertices.Channels.Add(VertexChannelNames.Weights(0), xnaWeights);
			}

			// Individual channels go here
			if (aiMesh.HasNormals)
				geom.Vertices.Channels.Add(VertexChannelNames.Normal(), aiMesh.Normals.Select(ToXna));

			for (var i = 0; i < aiMesh.TextureCoordinateChannelCount; i++)
				geom.Vertices.Channels.Add(VertexChannelNames.TextureCoordinate(i), aiMesh.TextureCoordinateChannels[i].Select(ToXnaTexCoord));

			for (var i = 0; i < aiMesh.VertexColorChannelCount; i++)
				geom.Vertices.Channels.Add(VertexChannelNames.Color(i), aiMesh.VertexColorChannels[i]);

			return geom;
		}

		/// <summary>
		/// Identifies the nodes that represent bones and stores the bone offset matrices.
		/// </summary>
		private void FindSkeleton()
		{
			// See http://assimp.sourceforge.net/lib_html/data.html, section "Bones"
			// and notes above.

			// First, identify all deformation bones.
			_deformationBones = FindDeformationBones(_scene);
			if (_deformationBones.Count == 0)
				return;

			// Walk the tree upwards to find the root bones.
			var rootBones = new HashSet<Node>();
			foreach (var boneName in _deformationBones.Keys)
				rootBones.Add(FindRootBone(_scene, boneName));

			if (rootBones.Count > 1)
				throw new Exception("Multiple skeletons found. Please ensure that the model does not contain more that one skeleton.");

			_rootBone = rootBones.First();

			// Add all nodes below root bone to skeleton.
			GetSubtree(_rootBone, _bones);
		}

		/// <summary>
		/// Finds the deformation bones (= bones attached to meshes).
		/// </summary>
		/// <param name="scene">The scene.</param>
		/// <returns>A dictionary of all deformation bones and their offset matrices.</returns>
		private static Dictionary<string, Matrix4x4> FindDeformationBones(Scene scene)
		{
			Debug.Assert(scene != null);

			var offsetMatrices = new Dictionary<string, Matrix4x4>();
			if (scene.HasMeshes)
				foreach (var mesh in scene.Meshes)
					if (mesh.HasBones)
						foreach (var bone in mesh.Bones)
							if (!offsetMatrices.ContainsKey(bone.Name))
								offsetMatrices[bone.Name] = ToXna(bone.OffsetMatrix);

			return offsetMatrices;
		}

		/// <summary>
		/// Finds the root bone of a specific bone in the skeleton.
		/// </summary>
		/// <param name="scene">The scene.</param>
		/// <param name="boneName">The name of a bone in the skeleton.</param>
		/// <returns>The root bone.</returns>
		private static Node FindRootBone(Scene scene, string boneName)
		{
			Debug.Assert(scene != null);
			Debug.Assert(!string.IsNullOrEmpty(boneName));

			// Start with the specified bone.
			Node node = scene.RootNode.FindNode(boneName);
			Debug.Assert(node != null, "Node referenced by mesh not found in model.");

			// Walk all the way up to the scene root or the mesh node.
			Node rootBone = node;
			while (node != scene.RootNode && !node.HasMeshes)
			{
				// Only when FBXPreservePivotsConfig(true):
				// The FBX path likes to put these extra preserve pivot nodes in here.
				if (!node.Name.Contains("$AssimpFbx$"))
					rootBone = node;

				node = node.Parent;
			}

			return rootBone;
		}

		/// <summary>
		/// Imports the skeleton including all skeletal animations.
		/// </summary>
		private void ImportSkeleton()
		{
			if (_rootBone == null)
				return;

			// Convert nodes to bones and attach to root node.
			var rootBoneContent = (BoneContent)ImportBones(_rootBone, _rootBone.Parent, null);
			_rootNode.Children.Add(rootBoneContent);

			if (!_scene.HasAnimations)
				return;

			// Convert animations and add to root bone.
			foreach (var animation in _scene.Animations)
			{
				var animationContent = ImportAnimation(animation);
				rootBoneContent.Animations.Add(animationContent.Name, animationContent);
			}
		}

		/// <summary>
		/// Converts the specified node and all descendant nodes to XNA bones.
		/// </summary>
		/// <param name="aiNode">The node.</param>
		/// <param name="aiParent">The parent node.</param>
		/// <param name="parent">The <paramref name="aiParent"/> node converted to XNA.</param>
		/// <returns>The XNA <see cref="NodeContent"/>.</returns>
		private NodeContent ImportBones(Node aiNode, Node aiParent, NodeContent parent)
		{
			Debug.Assert(aiNode != null);
			Debug.Assert(aiParent != null);

			NodeContent node = null;
			if (!aiNode.Name.Contains("_$AssimpFbx$")) // Ignore pivot nodes
			{
				const string mangling = "_$AssimpFbxNull$"; // Null leaf nodes are helpers

				if (aiNode.Name.Contains(mangling))
				{
					// Null leaf node
					node = new NodeContent
					{
						Name = aiNode.Name.Replace(mangling, string.Empty),
						Transform = ToXna(GetRelativeTransform(aiNode, aiParent))
					};
				}
				else if (_bones.Contains(aiNode))
				{
					// Bone
					node = new BoneContent
					{
						Name = aiNode.Name,
					};

					// node.Transform is irrelevant for bones. This transform is just the
					// pose of the node at the time of the export. This could, for example,
					// be one of the animation frames. It is not necessarily the bind pose
					// (rest pose)!
					// In XNA BoneContent.Transform needs to be set to the relative bind pose
					// Matrix4x4. The relative bind pose Matrix4x4 can be derived from the OffsetMatrix4x4
					// which is stored in aiMesh.Bones.
					//
					// offsetMatrix4x4 ... Offset Matrix4x4. Transforms the mesh from local space to bone space in bind pose.
					// bindPoseRel  ... Relative bind pose Matrix4x4. Defines the transform of a bone relative to its parent bone.
					// bindPoseAbs  ... Absolute bind pose Matrix4x4. Defines the transform of a bone relative to world space.
					//
					// The offset Matrix4x4 is the inverse of the absolute bind pose Matrix4x4.
					//   offsetMatrix4x4 = inverse(bindPoseAbs)
					//
					// bindPoseAbs = bindPoseRel * parentBindPoseAbs
					// => bindPoseRel = bindPoseAbs * inverse(parentBindPoseAbs)
					//                = inverse(offsetMatrix4x4) * parentOffsetMatrix4x4

					Matrix4x4 offsetMatrix4x4;
					Matrix4x4 parentOffsetMatrix4x4;
					bool isOffsetMatrix4x4Valid = _deformationBones.TryGetValue(aiNode.Name, out offsetMatrix4x4);
					bool isParentOffsetMatrix4x4Valid = _deformationBones.TryGetValue(aiParent.Name, out parentOffsetMatrix4x4);
					if (isOffsetMatrix4x4Valid && isParentOffsetMatrix4x4Valid)
					{
						node.Transform = offsetMatrix4x4.Invert() * parentOffsetMatrix4x4;
					}
					else if (isOffsetMatrix4x4Valid && aiNode == _rootBone)
					{
						// The current bone is the first in the chain.
						// The parent offset Matrix4x4 is missing. :(
						FbxPivot pivot;
						if (_pivots.TryGetValue(node.Name, out pivot))
						{
							// --> Use transformation pivot.
							node.Transform = pivot.GetTransform(null, null, null);
						}
						else
						{
							// --> Let's assume that parent's transform is Identity.
							node.Transform = offsetMatrix4x4.Invert();
						}
					}
					else if (isOffsetMatrix4x4Valid && aiParent == _rootBone)
					{
						// The current bone is the second bone in the chain.
						// The parent offset Matrix4x4 is missing. :(
						// --> Derive Matrix4x4 from parent bone, which is the root bone.
						parentOffsetMatrix4x4 = parent.Transform.Invert();
						node.Transform = offsetMatrix4x4.Invert() * parentOffsetMatrix4x4;
					}
					else
					{
						// Offset matrices are not provided by Assimp. :(
						// Let's hope that the skeleton was exported in bind pose.
						// (Otherwise we are just importing garbage.)
						node.Transform = ToXna(GetRelativeTransform(aiNode, aiParent));
					}
				}
			}

			if (node != null)
			{
				if (parent != null)
					parent.Children.Add(node);

				// For the children, this is the new parent.
				aiParent = aiNode;
				parent = node;
			}

			foreach (var child in aiNode.Children)
				ImportBones(child, aiParent, parent);

			return node;
		}

		/// <summary>
		/// Converts the specified animation to XNA.
		/// </summary>
		/// <param name="aiAnimation">The animation.</param>
		/// <param name="nodeName">An optional filter.</param>
		/// <returns>The animation converted to XNA.</returns>
		private AnimationContent ImportAnimation(Animation aiAnimation, string nodeName = null)
		{
			var animation = new AnimationContent
			{
				Name = GetAnimationName(aiAnimation.Name),
				Duration = TimeSpan.FromSeconds(aiAnimation.DurationInTicks / aiAnimation.TicksPerSecond)
			};

			// In Assimp animation channels may be split into separate channels.
			//   "nodeXyz" --> "nodeXyz_$AssimpFbx$_Translation",
			//                 "nodeXyz_$AssimpFbx$_Rotation",
			//                 "nodeXyz_$AssimpFbx$_Scaling"
			// Group animation channels by name (strip the "_$AssimpFbx$" part).
			IEnumerable<IGrouping<string, NodeAnimationChannel>> channelGroups;
			if (nodeName != null)
			{
				channelGroups = aiAnimation.NodeAnimationChannels
										   .Where(channel => nodeName == GetNodeName(channel.NodeName))
										   .GroupBy(channel => GetNodeName(channel.NodeName));
			}
			else
			{
				channelGroups = aiAnimation.NodeAnimationChannels
										   .GroupBy(channel => GetNodeName(channel.NodeName));
			}

			foreach (var channelGroup in channelGroups)
			{
				var boneName = channelGroup.Key;
				var channel = new AnimationChannel();

				// Get transformation pivot for current bone.
				FbxPivot pivot;
				if (!_pivots.TryGetValue(boneName, out pivot))
					pivot = FbxPivot.Default;

				var scaleKeys = EmptyVectorKeys;
				var rotationKeys = EmptyQuaternionKeys;
				var translationKeys = EmptyVectorKeys;

				foreach (var aiChannel in channelGroup)
				{
					if (aiChannel.NodeName.EndsWith("_$AssimpFbx$_Scaling"))
					{
						scaleKeys = aiChannel.ScalingKeys;

						Debug.Assert(pivot.Scaling.HasValue);
						Debug.Assert(!aiChannel.HasRotationKeys || (aiChannel.RotationKeyCount == 1 && (aiChannel.RotationKeys[0].Value == new Assimp.Quaternion(1, 0, 0, 0) || aiChannel.RotationKeys[0].Value == new Assimp.Quaternion(0, 0, 0, 0))));
						Debug.Assert(!aiChannel.HasPositionKeys || (aiChannel.PositionKeyCount == 1 && aiChannel.PositionKeys[0].Value == new Vector3D(0, 0, 0)));
					}
					else if (aiChannel.NodeName.EndsWith("_$AssimpFbx$_Rotation"))
					{
						rotationKeys = aiChannel.RotationKeys;

						Debug.Assert(pivot.Rotation.HasValue);
						Debug.Assert(!aiChannel.HasScalingKeys || (aiChannel.ScalingKeyCount == 1 && aiChannel.ScalingKeys[0].Value == new Vector3D(1, 1, 1)));
						Debug.Assert(!aiChannel.HasPositionKeys || (aiChannel.PositionKeyCount == 1 && aiChannel.PositionKeys[0].Value == new Vector3D(0, 0, 0)));
					}
					else if (aiChannel.NodeName.EndsWith("_$AssimpFbx$_Translation"))
					{
						translationKeys = aiChannel.PositionKeys;

						Debug.Assert(pivot.Translation.HasValue);
						Debug.Assert(!aiChannel.HasScalingKeys || (aiChannel.ScalingKeyCount == 1 && aiChannel.ScalingKeys[0].Value == new Vector3D(1, 1, 1)));
						Debug.Assert(!aiChannel.HasRotationKeys || (aiChannel.RotationKeyCount == 1 && (aiChannel.RotationKeys[0].Value == new Assimp.Quaternion(1, 0, 0, 0) || aiChannel.RotationKeys[0].Value == new Assimp.Quaternion(0, 0, 0, 0))));
					}
					else
					{
						scaleKeys = aiChannel.ScalingKeys;
						rotationKeys = aiChannel.RotationKeys;
						translationKeys = aiChannel.PositionKeys;
					}
				}

				// Get all unique keyframe times. (Assuming that no two key frames
				// have the same time, which is usually a safe assumption.)
				var times = scaleKeys.Select(k => k.Time)
									 .Union(rotationKeys.Select(k => k.Time))
									 .Union(translationKeys.Select(k => k.Time))
									 .OrderBy(t => t)
									 .ToList();

				Debug.Assert(times.Count == times.Distinct().Count(), "Sequences combined with Union() should not have duplicates.");

				int prevScaleIndex = -1;
				int prevRotationIndex = -1;
				int prevTranslationIndex = -1;
				double prevScaleTime = 0.0;
				double prevRotationTime = 0.0;
				double prevTranslationTime = 0.0;
				Vector3? prevScale = null;
				Quaternion? prevRotation = null;
				Vector3? prevTranslation = null;

				foreach (var time in times)
				{
					// Get scaling.
					Vector3? scale;
					int scaleIndex = scaleKeys.FindIndex(k => k.Time == time);
					if (scaleIndex != -1)
					{
						// Scaling key found.
						scale = ToXna(scaleKeys[scaleIndex].Value);
						prevScaleIndex = scaleIndex;
						prevScaleTime = time;
						prevScale = scale;
					}
					else
					{
						// No scaling key found.
						if (prevScaleIndex != -1 && prevScaleIndex + 1 < scaleKeys.Count)
						{
							// Lerp between previous and next scaling key.
							var nextScaleKey = scaleKeys[prevScaleIndex + 1];
							var nextScaleTime = nextScaleKey.Time;
							var nextScale = ToXna(nextScaleKey.Value);
							var amount = (float)((time - prevScaleTime) / (nextScaleTime - prevScaleTime));
							scale = Vector3.Lerp(prevScale.Value, nextScale, amount);
						}
						else
						{
							// Hold previous scaling value.
							scale = prevScale;
						}
					}

					// Get rotation.
					Quaternion? rotation;
					int rotationIndex = rotationKeys.FindIndex(k => k.Time == time);
					if (rotationIndex != -1)
					{
						// Rotation key found.
						rotation = ToXna(rotationKeys[rotationIndex].Value);
						prevRotationIndex = rotationIndex;
						prevRotationTime = time;
						prevRotation = rotation;
					}
					else
					{
						// No rotation key found.
						if (prevRotationIndex != -1 && prevRotationIndex + 1 < rotationKeys.Count)
						{
							// Lerp between previous and next rotation key.
							var nextRotationKey = rotationKeys[prevRotationIndex + 1];
							var nextRotationTime = nextRotationKey.Time;
							var nextRotation = ToXna(nextRotationKey.Value);
							var amount = (float)((time - prevRotationTime) / (nextRotationTime - prevRotationTime));
							rotation = Quaternion.Slerp(prevRotation.Value, nextRotation, amount);
						}
						else
						{
							// Hold previous rotation value.
							rotation = prevRotation;
						}
					}

					// Get translation.
					Vector3? translation;
					int translationIndex = translationKeys.FindIndex(k => k.Time == time);
					if (translationIndex != -1)
					{
						// Translation key found.
						translation = ToXna(translationKeys[translationIndex].Value);
						prevTranslationIndex = translationIndex;
						prevTranslationTime = time;
						prevTranslation = translation;
					}
					else
					{
						// No translation key found.
						if (prevTranslationIndex != -1 && prevTranslationIndex + 1 < translationKeys.Count)
						{
							// Lerp between previous and next translation key.
							var nextTranslationKey = translationKeys[prevTranslationIndex + 1];
							var nextTranslationTime = nextTranslationKey.Time;
							var nextTranslation = ToXna(nextTranslationKey.Value);
							var amount = (float)((time - prevTranslationTime) / (nextTranslationTime - prevTranslationTime));
							translation = Vector3.Lerp(prevTranslation.Value, nextTranslation, amount);
						}
						else
						{
							// Hold previous translation value.
							translation = prevTranslation;
						}
					}

					// Apply transformation pivot.
					var transform = pivot.GetTransform(scale, rotation, translation);

					long ticks = (long)(time * (TimeSpan.TicksPerSecond / aiAnimation.TicksPerSecond));
					channel.Add(new AnimationKeyframe(TimeSpan.FromTicks(ticks), transform));
				}

				animation.Channels[channelGroup.Key] = channel;
			}

			return animation;
		}

		/// <summary>
		/// Copies the current node and all descendant nodes into a list.
		/// </summary>
		/// <param name="node">The current node.</param>
		/// <param name="list">The list.</param>
		private static void GetSubtree(Node node, List<Node> list)
		{
			Debug.Assert(node != null);
			Debug.Assert(list != null);

			list.Add(node);
			foreach (var child in node.Children)
				GetSubtree(child, list);
		}

		/// <summary>
		/// Gets the transform of node relative to a specific ancestor node.
		/// </summary>
		/// <param name="node">The node.</param>
		/// <param name="ancestor">The ancestor node. Can be <see langword="null"/>.</param>
		/// <returns>
		/// The relative transform. If <paramref name="ancestor"/> is <see langword="null"/> the
		/// absolute transform of <paramref name="node"/> is returned.
		/// </returns>
		private static Assimp.Matrix4x4 GetRelativeTransform(Node node, Node ancestor)
		{
			Debug.Assert(node != null);

			// Get transform of node relative to ancestor.
			Assimp.Matrix4x4 transform = node.Transform;
			Node parent = node.Parent;
			while (parent != null && parent != ancestor)
			{
				transform *= parent.Transform;
				parent = parent.Parent;
			}

			if (parent == null && ancestor != null)
				throw new ArgumentException(string.Format("Node \"{0}\" is not an ancestor of \"{1}\".", ancestor.Name, node.Name));

			return transform;
		}

		/// <summary>
		/// Gets the animation name without the "AnimStack::" part.
		/// </summary>
		/// <param name="name">The mangled animation name.</param>
		/// <returns>The original animation name.</returns>
		private static string GetAnimationName(string name)
		{
			return name.Replace("AnimStack::", string.Empty);
		}

		/// <summary>
		/// Gets the node name without the "_$AssimpFbx$" part.
		/// </summary>
		/// <param name="name">The mangled node name.</param>
		/// <returns>The original node name.</returns>
		private static string GetNodeName(string name)
		{
			int index = name.IndexOf("_$AssimpFbx$", StringComparison.Ordinal);
			return (index >= 0) ? name.Remove(index) : name;
		}

		#region Conversion Helpers

		[DebuggerStepThrough]
		public static Matrix4x4 ToXna(Assimp.Matrix4x4 Matrix4x4)
		{
			var result =  System.Numerics.Matrix4x4.Identity;

			result.M11 = Matrix4x4.A1;
			result.M12 = Matrix4x4.B1;
			result.M13 = Matrix4x4.C1;
			result.M14 = Matrix4x4.D1;

			result.M21 = Matrix4x4.A2;
			result.M22 = Matrix4x4.B2;
			result.M23 = Matrix4x4.C2;
			result.M24 = Matrix4x4.D2;

			result.M31 = Matrix4x4.A3;
			result.M32 = Matrix4x4.B3;
			result.M33 = Matrix4x4.C3;
			result.M34 = Matrix4x4.D3;

			result.M41 = Matrix4x4.A4;
			result.M42 = Matrix4x4.B4;
			result.M43 = Matrix4x4.C4;
			result.M44 = Matrix4x4.D4;

			return result;
		}

		[DebuggerStepThrough]
		public static Vector2 ToXna(Vector2D vector)
		{
			return new Vector2(vector.X, vector.Y);
		}

		[DebuggerStepThrough]
		public static Vector3 ToXna(Vector3D vector)
		{
			return new Vector3(vector.X, vector.Y, vector.Z);
		}

		[DebuggerStepThrough]
		public static Quaternion ToXna(Assimp.Quaternion quaternion)
		{
			return new Quaternion(quaternion.X, quaternion.Y, quaternion.Z, quaternion.W);
		}

		[DebuggerStepThrough]
		public static Vector3 ToXna(Color4D color)
		{
			return new Vector3(color.R, color.G, color.B);
		}

		[DebuggerStepThrough]
		public static Vector2 ToXnaTexCoord(Vector3D vector)
		{
			return new Vector2(vector.X, vector.Y);
		}

		#endregion
	}
}
