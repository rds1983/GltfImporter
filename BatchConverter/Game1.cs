using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.IO;

namespace BatchConverter
{
	/// <summary>
	/// This is the main type for your game.
	/// </summary>
	public class Game1 : Game
	{
		private const string ContentFolder = @"D:\Projects\DigitalRune\Samples\Content\bin\MonoGame\Windows\Content";

		private readonly GraphicsDeviceManager _graphics;

		public Game1()
		{
			_graphics = new GraphicsDeviceManager(this)
			{
				PreferredBackBufferWidth = 1200,
				PreferredBackBufferHeight = 800
			};

			Window.AllowUserResizing = true;
			IsMouseVisible = true;
		}

		protected override void LoadContent()
		{
			var files = Directory.GetFiles(ContentFolder, "*.xnb", SearchOption.AllDirectories);

			foreach (var file in files)
			{
				var folder = Path.GetDirectoryName(file);
				var contentFile = Path.GetFileNameWithoutExtension(file);

				contentFile = Path.Combine(folder, contentFile);
				Model model;
				try
				{
					model = Content.Load<Model>(contentFile);
				}
				catch(Exception)
				{
					continue;
				}
			}
		}

		protected override void Update(GameTime gameTime)
		{
			base.Update(gameTime);
		}

		/// This is called when the game should draw itself.
		/// </summary>
		/// <param name="gameTime">Provides a snapshot of timing values.</param>
		protected override void Draw(GameTime gameTime)
		{
			GraphicsDevice.Clear(Color.CornflowerBlue);

			base.Draw(gameTime);
		}
	}
}