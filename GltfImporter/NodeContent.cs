// MonoGame - Copyright (C) MonoGame Foundation, Inc
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System.Numerics;

namespace Microsoft.Xna.Framework.Content.Pipeline
{
	/// <summary>
	/// Provides a base class for graphics types that define local coordinate systems.
	/// </summary>
	[System.Diagnostics.DebuggerDisplay("Node '{Name}'")]
	public class NodeContent : ContentItem
	{
		Matrix4x4 transform;
		NodeContent parent;
		NodeContentCollection children;
		AnimationContentDictionary animations;

		/// <summary>
		/// Gets the value of the local Transform property, multiplied by the AbsoluteTransform of the parent.
		/// </summary>
		public Matrix4x4 AbsoluteTransform
		{
			get
			{
				if (parent != null)
					return transform * parent.AbsoluteTransform;
				return transform;
			}
		}

		/// <summary>
		/// Gets the set of animations belonging to this node.
		/// </summary>
		public AnimationContentDictionary Animations
		{
			get
			{
				return animations;
			}
		}

		/// <summary>
		/// Gets the children of the NodeContent object.
		/// </summary>
		public NodeContentCollection Children
		{
			get
			{
				return children;
			}
		}

		/// <summary>
		/// Gets the parent of this NodeContent object.
		/// </summary>
		public NodeContent Parent
		{
			get
			{
				return parent;
			}
			set
			{
				parent = value;
			}
		}

		/// <summary>
		/// Gets the transform Matrix4x4 of the scene.
		/// The transform Matrix4x4 defines a local coordinate system for the content in addition to any children of this object.
		/// </summary>
		public Matrix4x4 Transform
		{
			get
			{
				return transform;
			}
			set
			{
				transform = value;
			}
		}

		/// <summary>
		/// Creates an instance of NodeContent.
		/// </summary>
		public NodeContent()
		{
			children = new NodeContentCollection(this);
			animations = new AnimationContentDictionary();
			Transform = Matrix4x4.Identity;
		}
	}
}
