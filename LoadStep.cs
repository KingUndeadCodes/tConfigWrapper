﻿using Gajatko.IniFiles;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SevenZip;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using tConfigWrapper.DataTemplates;
using Terraria;
using Terraria.Audio;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;

namespace tConfigWrapper {
	public static class LoadStep {
		public static string[] files; // Array of all mods, should be changed to only enabled mods but I am not doing any of that yet :)
		public static Action<string> loadProgressText; // Heading text during loading
		public static Action<float> loadProgress; // Progress bar during loading: 0-1 scale
		public static Action<string> loadSubProgressText; // Subtext during loading
		public static int taskCompletedCount; // Int used for tracking load progress during content loading
		internal static ConcurrentDictionary<int, ItemInfo> globalItemInfos = new ConcurrentDictionary<int, ItemInfo>(); // Dictionaries are selfexplanatory, concurrent so that multiple threads can access them without dying

		private static readonly ConcurrentDictionary<string, IniFileSection> recipeDict = new ConcurrentDictionary<string, IniFileSection>();
		private static readonly ConcurrentDictionary<string, ModItem> itemsToLoad = new ConcurrentDictionary<string, ModItem>();
		private static readonly ConcurrentDictionary<string, (ModTile tile, string texture)> tilesToLoad = new ConcurrentDictionary<string, (ModTile, string)>();
		private static readonly ConcurrentDictionary<string, ModNPC> npcsToLoad = new ConcurrentDictionary<string, ModNPC>();
		public static ConcurrentDictionary<ModTile, DisplayName> tileMapData = new ConcurrentDictionary<ModTile, DisplayName>();

		private static Mod mod => ModContent.GetInstance<tConfigWrapper>();

		public static void Setup() { // Method to load everything
			recipeDict.TryGetValue("", out _); // Sanity check to make sure it's initialized

			// Cringe reflection
			Assembly assembly = Assembly.GetAssembly(typeof(Mod));
			Type UILoadModsType = assembly.GetType("Terraria.ModLoader.UI.UILoadMods");

			object loadModsValue = assembly.GetType("Terraria.ModLoader.UI.Interface").GetField("loadMods", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
			MethodInfo LoadStageMethod = UILoadModsType.GetMethod("SetLoadStage", BindingFlags.Instance | BindingFlags.Public);
			PropertyInfo ProgressProperty = UILoadModsType.GetProperty("Progress", BindingFlags.Instance | BindingFlags.Public);
			PropertyInfo SubProgressTextProperty = UILoadModsType.GetProperty("SubProgressText", BindingFlags.Instance | BindingFlags.Public);

			loadProgressText = (string s) => LoadStageMethod.Invoke(loadModsValue, new object[] { s, -1 });
			loadProgress = (float f) => ProgressProperty.SetValue(loadModsValue, f);
			loadSubProgressText = (string s) => SubProgressTextProperty.SetValue(loadModsValue, s);

			loadProgressText?.Invoke("tConfig Wrapper: Loading Mods");
			loadProgress?.Invoke(0f);

			files = Directory.GetFiles(tConfigWrapper.ModsPath, "*.obj"); // Populates array with all mods
			foreach (var modName in files)
				mod.Logger.Debug($"tConfig Mod: {Path.GetFileNameWithoutExtension(modName)} is enabled!"); // Writes all mod names to logs

			for (int i = 0; i < files.Length; i++) { // Iterates through every mod
				loadProgressText?.Invoke($"tConfig Wrapper: Loading {Path.GetFileNameWithoutExtension(files[i])}"); // Sets heading text to display the mod being loaded
				mod.Logger.Debug($"Loading tConfig Mod: {Path.GetFileNameWithoutExtension(files[i])}"); // Logs the mod being loaded
				using (var finished = new CountdownEvent(1)) {
					using (SevenZipExtractor extractor = new SevenZipExtractor(files[i])) {
						bool CursedMod = extractor.ArchiveFileNames[0].Contains("Pickaxe+ v1.3a"); // Cursed mod bad

						if (CursedMod)
							loadSubProgressText?.Invoke("You are loading a cursed mod, it's not our fault it takes so long to load");

						mod.Logger.Debug($"Loading Content: {Path.GetFileNameWithoutExtension(files[i])}");

						ConcurrentDictionary<string, MemoryStream> streams = new ConcurrentDictionary<string, MemoryStream>();
						DecompressMod(files[i], extractor, streams); // Decompresses mods since .obj files are literally just 7z files

						// Clear dictionaries and task count or else stuff from other mods will interfere with the current mod being loaded
						itemsToLoad.Clear();
						tilesToLoad.Clear();
						npcsToLoad.Clear();
						taskCompletedCount = 0;

						// Slowass linq sorts content and then is assigned to individual threads
						IEnumerable<string> itemFiles = extractor.ArchiveFileNames.Where(name => name.Contains("\\Item\\") && Path.GetExtension(name) == ".ini");
						IEnumerable<string> npcFiles = extractor.ArchiveFileNames.Where(name => name.Contains("\\NPC\\") && Path.GetExtension(name) == ".ini");
						IEnumerable<string> tileFiles = extractor.ArchiveFileNames.Where(name => name.Contains("\\Tile\\") && Path.GetExtension(name) == ".ini");

						int contentCount = itemFiles.Count() + npcFiles.Count() + tileFiles.Count(); // Count all content in mod for accurate loading progress

						if (contentCount != 0)
						{
							Thread itemThread = new Thread(CreateItem);
							itemThread.Start(new object[] { itemFiles, Path.GetFileNameWithoutExtension(files[i]), files[i], finished, extractor, contentCount, streams });
							finished.AddCount();

							Thread npcThread = new Thread(CreateNPC);
							npcThread.Start(new object[] { npcFiles, Path.GetFileNameWithoutExtension(files[i]), files[i], finished, extractor, contentCount, streams });
							finished.AddCount();

							Thread tileThread = new Thread(CreateTile);
							tileThread.Start(new object[] { tileFiles, Path.GetFileNameWithoutExtension(files[i]), files[i], finished, extractor, contentCount, streams });
							finished.AddCount();

							finished.Signal();
							finished.Wait();
						}

						foreach (var memoryStream in streams) {
							memoryStream.Value.Dispose();
						}

						//Load content from dictionaries
						foreach (var item in itemsToLoad) {
							mod.AddItem(item.Key, item.Value);
						}

						foreach (var tile in tilesToLoad) {
							mod.AddTile(tile.Key, tile.Value.tile, tile.Value.texture);
						}

						foreach (var npc in npcsToLoad) {
							mod.AddNPC(npc.Key, npc.Value);
						}
					}
				}
				// this is for the obj (im scared of it)
				//stream.Position = 0L;
				//BinaryReader reader;
				//using (reader = new BinaryReader(stream))
				//{
				//	string modName = Path.GetFileName(files[i]).Split('.')[0];
				//	stream.Position = 0L;
				//	var version = new Version(reader.ReadString());

				//	// Don't know what these things are
				//	int modVersion;
				//	string modDLVersion, modURL;
				//	if (version >= new Version("0.20.5"))
				//		modVersion = reader.ReadInt32();

				//	if (version >= new Version("0.22.8") && reader.ReadBoolean())
				//	{
				//		modDLVersion = reader.ReadString();
				//		modURL = reader.ReadString();
				//	}

				//	reader.Close();
				//	stream.Close();
				//}

			}
			//Reset progress bar
			loadSubProgressText?.Invoke("");
			loadProgressText?.Invoke("Loading mod");
			loadProgress?.Invoke(0f);
		}

		private static void DecompressMod(string objPath, SevenZipExtractor extractor, ConcurrentDictionary<string, MemoryStream> streams) {
			List<string> fileNames = extractor.ArchiveFileNames.ToList();
			loadSubProgressText?.Invoke("Decompressing");
			double numThreads = Math.Min((double)ModContent.GetInstance<WrapperModConfig>().NumThreads, fileNames.Count);

			using (CountdownEvent decompressCount = new CountdownEvent(1)) {
				// Split the files into numThreads chunks
				var chunks = new List<List<string>>();
				int chunkSize = (int) Math.Round(fileNames.Count / numThreads, MidpointRounding.AwayFromZero);

				for (int i = 0; i < fileNames.Count; i += chunkSize) {
					chunks.Add(fileNames.GetRange(i, Math.Min(chunkSize, fileNames.Count - i)));
				}

				// Create threads and decompress the chunks
				foreach (var chunk in chunks) {
					ThreadPool.QueueUserWorkItem(DecompressMod, new object[] {objPath, chunk, streams, decompressCount});
					decompressCount.AddCount();
				}

				// Wait for the CountdownEvent to end
				decompressCount.Signal();
				decompressCount.Wait();
			}
		}

		public static int decompressTasksCompleted = 0; // Total number of items decompressed
		public static int decompressTotalFiles = 0; // Total number of items that need to be decompressed
		private static void DecompressMod(object callback) {
			// Process the parameters
			object[] parameters = (object[])callback;
			string objPath = parameters[0] as string;
			List<string> files = parameters[1] as List<string>;
			ConcurrentDictionary<string, MemoryStream> streams = parameters[2] as ConcurrentDictionary<string, MemoryStream>;
			CountdownEvent countdown = parameters[3] as CountdownEvent;

			// Create a FileStream with the following arguments to be able to have multiple threads access it
			using (FileStream fileStream = new FileStream(objPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
			using (SevenZipExtractor extractor = new SevenZipExtractor(fileStream)) {
				decompressTotalFiles += files.Count; // Counts the number of items that need to be loaded for accurate progress bar
				foreach (var fileName in files) {
					loadProgress?.Invoke((float)decompressTasksCompleted / decompressTotalFiles); // Sets the progress bar
					// If the extension is not valid, skip the file
					string extension = Path.GetExtension(fileName);
					if (!(extension == ".ini" || extension == ".cs" || extension == ".png" || extension == ".dll" || extension == ".obj"))
						continue;

					// Create a MemoryStream and extract the file
					MemoryStream stream = new MemoryStream();
					extractor.ExtractFile(fileName, stream);
					stream.Position = 0;
					streams.TryAdd(fileName, stream);
					decompressTasksCompleted++; // Increments the number of tasks completed for accurate progress display
				}
			}

			// Signal the end of the thread
			countdown.Signal();
		}

		public static void SetupRecipes() { // Sets up recipes, what were you expecting?
			loadProgressText.Invoke("tConfig Wrapper: Adding Recipes"); // Ah yes, more reflection
			loadProgress.Invoke(0f);
			int progressCount = 0;
			bool initialized = (bool)Assembly.GetAssembly(typeof(Mod)).GetType("Terraria.ModLoader.MapLoader").GetField("initialized", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null); // Check if the map is already initialized
			foreach (var iniFileSection in recipeDict) { // Load every recipe in the recipe dict
				progressCount++; // Count the number of recipes, still broken somehow :(
				string modName = iniFileSection.Key.Split(':')[0];
				ModRecipe recipe = null;
				if (initialized) // Only make the recipe if the maps have already been initialized. The checks for initialized are because I run this method in GetTileMapEntires() to see what tiles are used in recipes and need to have a name in their map entry
					recipe = new ModRecipe(mod);
				foreach (var element in iniFileSection.Value.elements) { // ini recipe loading, code is readable enough.
					string[] splitElement = element.Content.Split('=');
					string key = splitElement[0];
					string value = splitElement[1];
					switch (key) {
						case "Amount" when initialized: {
							int id;
							if ((id = ItemID.FromLegacyName(iniFileSection.Key.Split(':')[1], 4)) != 0)
								recipe?.SetResult(id, int.Parse(value));
							else
								recipe?.SetResult(mod, iniFileSection.Key, int.Parse(value));
							break;
						}
						case "needWater" when initialized:
							recipe.needWater = bool.Parse(value);
							break;
						case "Items" when initialized: {
							foreach (string recipeItem in value.Split(',')) {
								var recipeItemInfo = recipeItem.Split(null, 2);
								int amount = int.Parse(recipeItemInfo[0]);

								int itemID = mod.ItemType($"{modName}:{recipeItemInfo[1]}");
								if (itemID == 0)
									itemID = ItemID.FromLegacyName(recipeItemInfo[1], 4);

								var numberIngredients =
									recipe?.requiredItem.Count(i => i != null & i.type != ItemID.None);
								if (numberIngredients < 14)
									recipe?.AddIngredient(itemID, amount);
								else {
									mod.Logger.Debug($"The following item has exceeded the max ingredient limit! -> {iniFileSection.Key}");
									tConfigWrapper.ReportErrors = true;
								}
							}
							break;
						}
						case "Tiles": { // Does stuff to check for modtiles and vanilla tiles that have changed their name since 1.1.2
							foreach (string recipeTile in value.Split(',')) {
								string noSpaceTile = recipeTile.Replace(" ", "");
								int tileInt = mod.TileType($"{modName}:{noSpaceTile}");
								var tileModTile = mod.GetTile($"{modName}:{noSpaceTile}");
								if (!TileID.Search.ContainsName(noSpaceTile) && !CheckStringConversion(noSpaceTile) &&
									tileInt == 0) {
									if (initialized) {
										mod.Logger.Debug($"TileID {noSpaceTile} does not exist"); // We will have to manually convert anything that breaks lmao
										tConfigWrapper.ReportErrors = true;
									}
								}
								else if (CheckStringConversion(noSpaceTile)) {
									string converted = ConvertTileStringTo13(noSpaceTile);
									if (initialized)
										recipe?.AddTile(TileID.Search.GetId(converted));
								}
								else if (tileInt != 0) {
									if (initialized) {
										recipe?.AddTile(tileModTile);
										mod.Logger.Debug($"{modName}:{noSpaceTile} added to recipe through mod.TileType!");
									}
									else {
										DisplayName preserveName = tileMapData[tileModTile]; // DisplayName is a struct so I can store 3 values in one dictionary
										tileMapData[tileModTile] = new DisplayName(true, preserveName.Name); // I'd like to be able to do something like tileMapData[tileModTile].DoDisplay = true but I can't because readonly :letsfu:
									}
								}
								else if (initialized)
									recipe?.AddTile(TileID.Search.GetId(noSpaceTile));
							}

							break;
						}
					}
				}

				if (recipe?.createItem != null && recipe?.createItem.type != ItemID.None && initialized)
					recipe?.AddRecipe();
				loadProgress.Invoke(progressCount / recipeDict.Count);
			}
		}
		/// <summary>
		/// Converts tile strings to the 1.3 equivalent
		/// </summary>
		/// <param name="noSpaceTile"></param>
		/// <returns></returns>
		private static string ConvertTileStringTo13(string noSpaceTile) {
			switch (noSpaceTile) {
				case "Anvil":
					return "Anvils";
				case "WorkBench":
				case "Workbench":
					return "WorkBenches";
				case "Furnace":
					return "Furnaces";
				case "Tinkerer'sWorkshop":
					return "TinkerersWorkbench";
				case "Bottle":
					return "Bottles";
				case "Bookcase":
					return "Bookcases";
				case "Table":
					return "Tables";
				default:
					return noSpaceTile;
			}
		}
		/// <summary>
		/// Checks if a tile can be converted to a 1.3 string
		/// </summary>
		/// <param name="noSpaceTile"></param>
		/// <returns></returns>
		private static bool CheckStringConversion(string noSpaceTile) {
			switch (noSpaceTile) {
				case "Anvil":
				case "WorkBench":
				case "Workbench":
				case "Furnace":
				case "Tinkerer'sWorkshop":
				case "Bottle":
				case "Bookcase":
				case "Table":
					return true;
				default:
					return false;
			}
		}

		private static void CreateItem(object stateInfo) { // This is literally just to simplify the threading and counting of content loading
			object[] parameters = (object[])stateInfo;
			CountdownEvent countdown = (CountdownEvent)parameters[3];

			foreach (var fileName in (IEnumerable<string>)parameters[0]) {
				loadSubProgressText?.Invoke(fileName);
				CreateItem(fileName, (string)parameters[1], (string)parameters[2], (ConcurrentDictionary<string, MemoryStream>)parameters[6]);
				taskCompletedCount++;
				loadProgress?.Invoke((float)taskCompletedCount / (int)parameters[5]);
			}
			countdown.Signal();
		}

		private static void CreateItem(string fileName, string modName, string extractPath, ConcurrentDictionary<string, MemoryStream> streams) { // Loads content, I don't know how it works either
			MemoryStream iniStream = streams[fileName];

			IniFileReader reader = new IniFileReader(iniStream);
			IniFile iniFile = IniFile.FromStream(reader);

			object info = new ItemInfo();
			List<string> toolTipList = new List<string>();

			// Get the mod name
			string itemName = Path.GetFileNameWithoutExtension(fileName);
			string internalName = $"{modName}:{itemName}";
			bool logItemAndModName = false;

			foreach (IniFileSection section in iniFile.sections) {
				foreach (IniFileElement element in section.elements) {
					switch (section.Name) {
						case "Stats": {
							var splitElement = element.Content.Split('=');

							var statField = typeof(ItemInfo).GetField(splitElement[0]);

							switch (splitElement[0]) {
								// Set the tooltip, has to be done manually since the toolTip field doesn't exist in 1.3
								case "toolTip":
								case "toolTip1":
								case "toolTip2":
								case "toolTip3":
								case "toolTip4":
								case "toolTip5":
								case "toolTip6":
								case "toolTip7": {
									toolTipList.Add(splitElement[1]);
									continue;
								}
								case "useSound": {
									var soundStyleId = int.Parse(splitElement[1]);
									var soundStyle = new LegacySoundStyle(2, soundStyleId); // All items use the second sound ID
									statField = typeof(ItemInfo).GetField("UseSound");
									statField.SetValue(info, soundStyle);
									continue;
								}
								case "createTileName": {
									statField = typeof(ItemInfo).GetField("createTile");
									var succeed = int.TryParse(splitElement[1], out var createTileID);
									if (succeed) {
										statField.SetValue(info, createTileID);
										mod.Logger.Debug($"TileID {createTileID} was sucessfully parsed!");
										logItemAndModName = true;
									}
									else {
										int modTile = mod.TileType($"{modName}:{fileName}");
										if (modTile != 0) {
											statField.SetValue(info, modTile);
											mod.Logger.Debug($"Mod tile {modTile} was successfully added");
											logItemAndModName = true;
										}
										else {
											mod.Logger.Debug($"TryParse & mod.TileType: Failed to parse the placeable tile! -> {splitElement[1]}");
											logItemAndModName = true;
										}
									}

									continue;
								}
								case "type":
									continue;
								default: {
									if (statField == null) {
										mod.Logger.Debug($"Item field not found or invalid field! -> {splitElement[0]}");
										logItemAndModName = true;
										tConfigWrapper.ReportErrors = true;
										continue;
									}

									break;
								}
							}

							// Convert the value to an object of type statField.FieldType
							TypeConverter converter = TypeDescriptor.GetConverter(statField.FieldType);
							object realValue = converter.ConvertFromString(splitElement[1]);
							statField.SetValue(info, realValue);
							break;
						}
						case "Recipe": {
							if (!recipeDict.ContainsKey(internalName))
								recipeDict.TryAdd(internalName, section);
							break;
						}
					}
				}
			}

			if (logItemAndModName)
				mod.Logger.Debug($"{modName}: {itemName}"); //Logs the item and mod name if "Field not found or invalid field". Mod and item name show up below the other log line

			string toolTip = null;

			foreach (string toolTipLine in toolTipList) {
				toolTip += "\n" + toolTipLine;
			}

			// Check if a texture for the .ini file exists
			string texturePath = Path.ChangeExtension(fileName, "png");
			Texture2D itemTexture = null;
			if (!Main.dedServ && streams.TryGetValue(texturePath, out MemoryStream textureStream)) {
				itemTexture = Texture2D.FromStream(Main.instance.GraphicsDevice, textureStream); // Load a Texture2D from the stream
			}

			int id;
			if ((id = ItemID.FromLegacyName(itemName, 4)) != 0) {
				if (!globalItemInfos.ContainsKey(id))
					globalItemInfos.TryAdd(id, (ItemInfo)info);
				else
					globalItemInfos[id] = (ItemInfo)info;

				reader.Dispose();
				return;
			}

			if (itemTexture != null)
				itemsToLoad.TryAdd(internalName, new BaseItem((ItemInfo)info, itemName, toolTip, itemTexture));
			else
				itemsToLoad.TryAdd(internalName, new BaseItem((ItemInfo)info, itemName, toolTip));

			reader.Dispose();
			//}
		}

		private static void CreateNPC(object stateInfo) { // This is literally just to simplify threading and progress counting
			object[] parameters = (object[])stateInfo;
			CountdownEvent countdown = (CountdownEvent)parameters[3];

			foreach (var fileName in (IEnumerable<string>)parameters[0]) {
				loadSubProgressText?.Invoke(fileName);
				CreateNPC(fileName, (string)parameters[1], (string)parameters[2], (ConcurrentDictionary<string, MemoryStream>)parameters[6]);
				taskCompletedCount++;
				loadProgress?.Invoke((float)taskCompletedCount / (int)parameters[5]);
			}
			countdown.Signal();
		}

		private static void CreateNPC(string fileName, string modName, string extractPath, ConcurrentDictionary<string, MemoryStream> streams) { // I don't know how this works either
			MemoryStream iniStream = streams[fileName];

			IniFileReader reader = new IniFileReader(iniStream);
			IniFile iniFile = IniFile.FromStream(reader);

			object info = new NpcInfo();

			string npcName = Path.GetFileNameWithoutExtension(fileName);
			string internalName = $"{modName}:{npcName}";
			bool logNPCAndModName = false;

			foreach (IniFileSection section in iniFile.sections) {
				foreach (IniFileElement element in section.elements) {
					switch (section.Name) {
						case "Stats": {
							var splitElement = element.Content.Split('=');

							string split1Correct = ConvertField13(splitElement[0]);
							var statField = typeof(NpcInfo).GetField(split1Correct);

							switch (splitElement[0]) {
								case "soundHit": {
									var soundStyleID = int.Parse(splitElement[1]);
									var soundStyle = new LegacySoundStyle(3, soundStyleID); // All NPC hit sounds use 3
									statField = typeof(NpcInfo).GetField("HitSound");
									statField.SetValue(info, soundStyle);
									continue;
								}
								case "soundKilled": {
									var soundStyleID = int.Parse(splitElement[1]);
									var soundStyle = new LegacySoundStyle(4, soundStyleID); // All death sounds use 4
									statField = typeof(NpcInfo).GetField("DeathSound");
									statField.SetValue(info, soundStyle);
									continue;
								}
								case "type":
									continue;
								default: {
									if (statField == null) {
										mod.Logger.Debug($"NPC field not found or invalid field! -> {splitElement[0]}");
										logNPCAndModName = true;
										tConfigWrapper.ReportErrors = true;
										continue;
									}

									break;
								}
							}

							TypeConverter converter = TypeDescriptor.GetConverter(statField.FieldType);
							object realValue = converter.ConvertFromString(splitElement[1]);
							statField.SetValue(info, realValue);
							break;
						}
						case "BuffImmunities":
							// do
							break;
						case "Drops":
							// do
							break;
					}
				}
			}

			if (logNPCAndModName)
				mod.Logger.Debug($"{modName}: {npcName}"); //Logs the npc and mod name if "Field not found or invalid field". Mod and npc name show up below the other log line

			// Check if a texture for the .ini file exists
			string texturePath = Path.ChangeExtension(fileName, "png");
			Texture2D npcTexture = null;
			if (!Main.dedServ && streams.TryGetValue(texturePath, out MemoryStream textureStream)) {
				npcTexture = Texture2D.FromStream(Main.instance.GraphicsDevice, textureStream); // Load a Texture2D from the stream
			}

			if (npcTexture != null)
				npcsToLoad.TryAdd(internalName, new BaseNPC((NpcInfo)info, npcName, npcTexture));
			else
				npcsToLoad.TryAdd(internalName, new BaseNPC((NpcInfo)info, npcName));

			reader.Dispose();
		}

		/// <summary>
		/// Converts fields to the 1.3 equivalent
		/// </summary>
		/// <param name="splitElement"></param>
		/// <returns></returns>
		private static string ConvertField13(string splitElement) {
			switch (splitElement) {
				case "knockBackResist":
					return "knockBackResist";
				case "hitSoundList":
					return "soundStyle";
				case "hitSound":
					return "soundType";
				case "pick":
				case "axe":
				case "hammer":
					return "mineResist";
				case "Shine":
					return "tileShine";
				case "Shine2":
					return "tileShine2";
				case "Lighted":
					return "tileLighted";
				case "MergeDirt":
					return "tileMergeDirt";
				case "Cut":
					return "tileCut";
				case "Alch":
					return "tileAlch";
				case "Stone":
					return "tileStone";
				case "WaterDeath":
					return "tileWaterDeath";
				case "LavaDeath":
					return "tileLavaDeath";
				case "Table":
					return "table";
				case "BlockLight":
					return "tileBlockLight";
				case "NoSunLight":
					return "tileNoSunLight";
				case "Dungeon":
					return "tileDungeon";
				case "SolidTop":
					return "tileSolidTop";
				case "Solid":
					return "tileSolid";
				case "NoAttach":
					return "tileNoAttach";
				default:
					return splitElement;
			}
		}

		private static void CreateTile(object stateInfo) { // This is literally for easier multithreading again
			object[] parameters = (object[])stateInfo;
			CountdownEvent countdown = (CountdownEvent)parameters[3];

			foreach (var fileName in (IEnumerable<string>)parameters[0]) {
				loadSubProgressText?.Invoke(fileName);
				CreateTile(fileName, (string)parameters[1], (string)parameters[2], (ConcurrentDictionary<string, MemoryStream>)parameters[6]);
				taskCompletedCount++;
				loadProgress?.Invoke((float)taskCompletedCount / (int)parameters[5]);
			}
			countdown.Signal();
		}

		private static void CreateTile(string fileName, string modName, string extractPath, ConcurrentDictionary<string, MemoryStream> streams) { // I have no idea how this works either
			Dictionary<string, int> tileNumberFields = new Dictionary<string, int>();
			Dictionary<string, bool> tileBoolFields = new Dictionary<string, bool>();
			Dictionary<string, string> tileStringFields = new Dictionary<string, string>();

			MemoryStream iniStream = streams[fileName];

			IniFileReader reader = new IniFileReader(iniStream);
			IniFile iniFile = IniFile.FromStream(reader);

			object info = new TileInfo();

			string displayName = Path.GetFileNameWithoutExtension(fileName);
			string internalName = $"{modName}:{displayName.Replace(" ", "").Replace("'", "")}";
			bool logTileAndName = false;
			bool oreTile = false;

			foreach (IniFileSection section in iniFile.sections) {
				foreach (IniFileElement element in section.elements) {
					if (section.Name == "Stats") {
						var splitElement = element.Content.Split('=');

						string converted = ConvertField13(splitElement[0]);
						var statField = typeof(TileInfo).GetField(converted);

						if ((converted == "tileShine" && splitElement[1] != "0") || displayName.Contains("Ore"))
							oreTile = true;

						switch (converted) {
							case "DropName":
								splitElement[1] = splitElement[1].Replace(" ", "");
								statField = typeof(TileInfo).GetField("drop");
								statField.SetValue(info, mod.TileType(splitElement[1]));
								continue;
							case "minPick":
							case "minAxe":
							case "minHammer": {
								if (converted == "minAxe")
									splitElement[1] = (int.Parse(splitElement[1]) * 5).ToString();
								statField = typeof(TileInfo).GetField("minPick");
								int splitInt = int.Parse(splitElement[1]);
								statField.SetValue(info, splitInt);
								continue;
							}
							case "Width":
							case "Height":
							case "tileShine":
								tileNumberFields.Add(converted, int.Parse(splitElement[1]));
								continue;
							case "tileLighted":
							case "tileMergeDirt":
							case "tileCut":
							case "tileAlch":
							case "tileShine2":
							case "tileStone":
							case "tileWaterDeath":
							case "tileLavaDeath":
							case "tileTable":
							case "tileBlockLight":
							case "tileNoSunLight":
							case "tileDungeon":
							case "tileSolidTop":
							case "tileSolid":
							case "tileNoAttach":
							case "tileNoFail":
							//else if (converted == "furniture")  {
							//	tileStringFields.Add(converted, splitElement[1]);
							//	continue;
							//}
							case "tileFrameImportant":
								tileBoolFields.Add(converted, bool.Parse(splitElement[1]));
								continue;
							case "id":
							case "type":
							case "mineResist" when splitElement[1] == "0":
								continue;
							default: {
								if (statField == null) {
									mod.Logger.Debug($"Tile field not found or invalid field! -> {converted}");
									logTileAndName = true;
									tConfigWrapper.ReportErrors = true;
									continue;
								}

								break;
							}
						}

						TypeConverter converter = TypeDescriptor.GetConverter(statField.FieldType);
						object realValue = converter.ConvertFromString(splitElement[1]);
						statField.SetValue(info, realValue);
					}
				}
			}

			string texturePath = Path.ChangeExtension(fileName, "png");
			Texture2D tileTexture = null;
			if (!Main.dedServ && streams.TryGetValue(texturePath, out MemoryStream textureStream)) {
				tileTexture = Texture2D.FromStream(Main.instance.GraphicsDevice, textureStream);
			}

			if (tileTexture != null) {
				BaseTile baseTile = new BaseTile((TileInfo)info, internalName, tileTexture, tileBoolFields, tileNumberFields, tileStringFields);
				tilesToLoad.TryAdd(internalName, (baseTile, "tConfigWrapper/DataTemplates/MissingTexture"));
				// Adds names to tiles that are ores
				if (oreTile)
					tileMapData.TryAdd(baseTile, new DisplayName(true, displayName));
				else
					tileMapData.TryAdd(baseTile, new DisplayName(false, displayName));
			}

			if (logTileAndName)
				mod.Logger.Debug($"{modName}: {displayName}"); //Logs the tile and mod name if "Field not found or invalid field". Mod and tile name show up below the other log lines
		}

		public static void GetTileMapEntries() { // Loads tile map entries :\
			loadProgressText?.Invoke("tConfig Wrapper: Loading Map Entries");
			loadProgress?.Invoke(0f);
			SetupRecipes(); // Check what tiles are used in recipes so it can add a name to it
			int iterationCount = 0;
			foreach (var modTile in tileMapData) {
				iterationCount++;
				loadSubProgressText?.Invoke($"{modTile.Value.Name}");
				Texture2D tileTex = Main.tileTexture[modTile.Key.Type];
				Color[] colors = new Color[tileTex.Width * tileTex.Height];
				tileTex.GetData(colors);
				int r = colors.Sum(x => x.R) / colors.Length;
				int g = colors.Sum(x => x.G) / colors.Length;
				int b = colors.Sum(x => x.B) / colors.Length;
				var mainColor = colors.GroupBy(col => new Color(col.R, col.G, col.B))
					.OrderByDescending(grp => grp.Count())
					.Where(grp => grp.Key.R != 0 || grp.Key.G != 0 || grp.Key.B != 0)
					.Select(grp => grp.Key)
					.First();
				if (modTile.Value.DoDisplayName) {
					modTile.Key.AddMapEntry(mainColor, Language.GetText(modTile.Value.Name));
					mod.Logger.Debug($"Added translation and color for {modTile.Value.Name}");
				}
				else {
					modTile.Key.AddMapEntry(mainColor);
					mod.Logger.Debug($"Added color for {modTile.Value.Name}");
				}
				loadProgress?.Invoke(iterationCount / tileMapData.Count);
			}
		}
	}
}