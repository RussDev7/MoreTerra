﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.ComponentModel;
using System.Timers;
using MoreTerra.Utilities;
using System.Text;

namespace MoreTerra.Structures
{
	public class World
	{
		// All the parts of a Terraria world.
		private WorldHeader header;
		private Tile[,] tiles;
		private List<Chest> chests;
		private List<Sign> signs;
		private List<NPC> npcs;
		private Footer footer;

		// Various file reading items.
		private BackwardsBinaryReader backReader;
		private BufferedBinaryReader buffReader;
		private BinaryReader reader;
		private FileStream stream;

		// Quick lookup for the world sizes.
		private int MaxX, MaxY;

		// Things to do with showing progress when reading/scanning.
		private int progress;
		private long progressPosition;
		private BackgroundWorker bw;

		// Lookup table for important tiles.
		private Boolean[] tileImportant;

		// Positions of each spot in the file.
		private Int64 posTiles;
		private Int64 posChests;
		private Int64 posSigns;
		private Int64 posNpcs;
		private Int64 posFooter;
		private Int64 posEnd;

		private Single readWorldPerc = 50;

		int[] sectionPointers;

		private int version;

		// List generated from reading in chest tiles.
		private Dictionary<Point, ChestType> chestTypeList;

		public static Point SpawnpointGlobal;

		#region Structures
		// Helper structure for storing information for tile scanning.
		public class TileImportance
		{
			private Boolean[] tImportant;
			private Boolean[] tKnown;

			public TileImportance()
			{
				Int32 i;

				tImportant = new Boolean[256];
				tKnown = new Boolean[256];

				for (i = 0; i < 256; i++)
				{
					tImportant[i] = false;
					tKnown[i] = false;
				}
			}

			public TileImportance(TileImportance copyFrom)
			{
				Int32 i;

				tImportant = new Boolean[256];
				tKnown = new Boolean[256];

				for (i = 0; i < 256; i++)
				{
					tImportant[i] = copyFrom.tImportant[i];
					tKnown[i] = copyFrom.tKnown[i];
				}
			}

			public Boolean isImportant(Byte i)
			{
				return tImportant[i];
			}

			public void setImportant(Byte i, Boolean v)
			{
				tImportant[i] = v;
			}

			public Boolean isKnown(Byte i)
			{
				return tKnown[i];
			}

			public void setKnown(Byte i, Boolean v)
			{
				tKnown[i] = v;
			}
		}

		// Helper structure for storing all the information
		// for each path when scanning for tiles.
		public class tileReader
		{
			public static Int32 nextId = 0;
			public Int32 id;
			public Int64 filePos;
			public TileImportance tileLookup;
			public Tile tile;
			public List<Byte> tileOrder;
			public Int32 tilesRead;

			public Point startAt;
			public Byte splitAt;

			public tileReader(Int64 fPos)
			{
				id = nextId;
				nextId++;

				filePos = fPos;

				tileLookup = new TileImportance();
				tile = new Tile();
				tileOrder = new List<Byte>();

				tilesRead = 0;
			}

			public tileReader(tileReader copy)
			{
				id = nextId;
				nextId++;

				filePos = copy.filePos;

				tileLookup = new TileImportance(copy.tileLookup);
				tile = new Tile(copy.tile);
				tileOrder = new List<Byte>();

				tilesRead = copy.tilesRead;

				foreach (Byte b in copy.tileOrder)
					tileOrder.Add(b);
			}
		}
		#endregion

		#region Constructors
		public World()
		{
			Clear();

			Initialize();
		}
		#endregion

		private void Initialize()
		{

			//	tileImportant = new Boolean[TileProperties.TYPES];

			//	for (Int32 i = 0; i < TileProperties.TYPES; i++)
			//		tileImportant[i] = TileProperties.tileTypeDefs[i].IsImportant;
		}

		public void Clear()
		{
			header = new WorldHeader();
			tiles = null;
			chests = null;
			signs = null;
			footer = null;

			posChests = 0;
			posEnd = 0;
			posFooter = 0;
			posNpcs = 0;
			posSigns = 0;
			posTiles = 0;

			MaxX = 0;
			MaxY = 0;
			progress = 0;

			if (reader != null)
			{
				reader.Close();
				reader = null;
			}

			if (stream != null)
				stream = null;

			if (buffReader != null)
				buffReader = null;

			if (bw != null)
				bw = null;
		}

		#region ReadFunctions

		public void ReadWorld(String world, BackgroundWorker worker = null)
		{
			Timer t = null;
#if (DEBUG == false)
			try
			{
#endif
			if (worker != null)
			{
				bw = worker;
				progressPosition = 0;
				t = new Timer(333);
				t.Elapsed += new ElapsedEventHandler(timer_ReadWorld);
				t.Start();
			}

			readWorldPerc = 50;

			stream = new FileStream(world, FileMode.Open, FileAccess.Read);
			reader = new BinaryReader(stream);

			ReadHeader();
			ReadWorldTiles();
			ReadChests();
			ReadSigns();
			ReadNPCs();
			ReadFooter();
#if (DEBUG == false)
			}
			catch (Exception e)
			{
				if (bw != null)
				{
					t.Stop();
					bw = null;
				}

				reader.Close();

				throw e;
			}
#endif

			if (bw != null)
			{
				t.Stop();
				bw = null;
			}

			reader.Close();
		}

		private void timer_ReadWorld(object sender, ElapsedEventArgs e)
		{
			progress = (Int32)(((Single)progressPosition / stream.Length) * readWorldPerc);
			bw.ReportProgress(progress);
		}

		private void ReadHeader()
		{
			version = reader.ReadInt32();
			if (version < 94)
				throw new Exception("Must use version 1.2.3.1 or higher!");

			ulong fileFormat = reader.ReadUInt64();

			byte fileTypeValue = (byte)(fileFormat >> 56 & (ulong)byte.MaxValue);
			//Revision
			uint revision = reader.ReadUInt32();

			//Favorite
			ulong favorite = reader.ReadUInt64();

			short sectionCount = reader.ReadInt16();
			sectionPointers = new int[sectionCount];
			for (int j = 0; j < sectionCount; j++)
			{
				sectionPointers[j] = reader.ReadInt32();
			}

			short tiletypeCount = reader.ReadInt16();
			tileImportant = new bool[tiletypeCount];
			byte flags = 0;
			byte mask = 0x80;

			for (int k = 0; k < tiletypeCount; k++)
			{
				if (mask == 0x80)
				{
					flags = reader.ReadByte();
					mask = 0x01;
				}
				else
				{
					mask <<= 1;
				}

				if ((flags & mask) == mask)
					tileImportant[k] = true;


			}

			Int32 x, y, w, h;
			Int32 i;

			if (bw != null)
				bw.ReportProgress((Int32)(((Single)progressPosition / stream.Length) * readWorldPerc)
					, "Reading Header");

			header = new WorldHeader();
			header.sectionPointers = sectionPointers;
			header.ReleaseNumber = version;
			header.Name = reader.ReadString();
			reader.ReadString();
			var genVersion = reader.ReadUInt64();
			var headerId = new Guid(reader.ReadBytes(16));
			reader.ReadInt32();
			x = reader.ReadInt32();
			w = reader.ReadInt32();
			y = reader.ReadInt32();
			h = reader.ReadInt32();

			header.WorldCoords = new Rect(x, w, y, h);


			MaxY = reader.ReadInt32();
			MaxX = reader.ReadInt32();

			header.MaxTiles = new Point(MaxX, MaxY);

			//Expert Mode
			bool expertMode;
			if (version >= 112)
				expertMode = reader.ReadBoolean();

			//Creation Time
			DateTime creationTime;
			if (version >= 141)
				creationTime = DateTime.FromBinary(reader.ReadInt64());

			// 1.4 World Headers
			if (version >= 222) { header.DrunkWorld = reader.ReadBoolean(); }
			if (version >= 227) { header.GoodWorld = reader.ReadBoolean(); }
			if (version >= 238) { header.TenthAnniversaryWorld = reader.ReadBoolean(); }
			if (version >= 239) { header.DontStarveWorld = reader.ReadBoolean(); }
			if (version >= 241) { header.NotTheBeesWorld = reader.ReadBoolean(); }

			header.TreeX = new int[3];
			header.TreeStyle = new int[4];
			header.CaveBackX = new int[3];
			header.CaveBackStyle = new int[4];
			header.MoonType = reader.ReadByte();

			for (i = 0; i < 3; i++)
			{
				header.TreeX[i] = reader.ReadInt32();
			}
			for (i = 0; i < 4; i++)
			{
				header.TreeStyle[i] = reader.ReadInt32();
			}
			for (i = 0; i < 3; i++)
			{
				header.CaveBackX[i] = reader.ReadInt32();
			}
			for (i = 0; i < 4; i++)
			{
				header.CaveBackStyle[i] = reader.ReadInt32();
			}
			header.IceBackStyle = reader.ReadInt32();
			header.JungleBackStyle = reader.ReadInt32();
			header.HellBackStyle = reader.ReadInt32();

			// 1.4 Fix
			if (version > 194)
			{
				reader.BaseStream.Position = 311;
			}

			header.SpawnPoint = new Point(reader.ReadInt32(), reader.ReadInt32());
			header.SurfaceLevel = reader.ReadDouble();
			header.RockLayer = reader.ReadDouble();
			header.TemporaryTime = reader.ReadDouble();
			header.IsDayTime = reader.ReadBoolean();
			header.MoonPhase = reader.ReadInt32();
			header.IsBloodMoon = reader.ReadBoolean();
			header.IsEclipse = reader.ReadBoolean();

			header.DungeonPoint = new Point(reader.ReadInt32(), reader.ReadInt32());
			header.Crimson = reader.ReadBoolean();
			header.IsBoss1Dead = reader.ReadBoolean();
			header.IsBoss2Dead = reader.ReadBoolean();
			header.IsBoss3Dead = reader.ReadBoolean();

			header.IsQueenBeeDead = reader.ReadBoolean();
			header.IsMechBoss1Dead = reader.ReadBoolean();
			header.IsMechBoss2Dead = reader.ReadBoolean();
			header.IsMechBoss3Dead = reader.ReadBoolean();
			header.IsMechBossAnyDead = reader.ReadBoolean();
			header.IsPlantBossDead = reader.ReadBoolean();
			header.IsGolemBossDead = reader.ReadBoolean();

			if (version >= 118)
				header.IsSlimeKingDead = reader.ReadBoolean();

			header.IsGoblinSaved = reader.ReadBoolean();
			header.IsWizardSaved = reader.ReadBoolean();
			header.IsMechanicSaved = reader.ReadBoolean();
			header.IsGoblinArmyDefeated = reader.ReadBoolean();
			header.IsClownDefeated = reader.ReadBoolean();
			header.IsFrostDefeated = reader.ReadBoolean();
			header.IsPiratesDefeated = reader.ReadBoolean();
			header.IsShadowOrbSmashed = reader.ReadBoolean();
			header.IsMeteorSpawned = reader.ReadBoolean();
			header.ShadowOrbsSmashed = reader.ReadByte();

			header.AltarsDestroyed = reader.ReadInt32();
			header.HardMode = reader.ReadBoolean();

			header.InvasionDelay = reader.ReadInt32();
			header.InvasionSize = reader.ReadInt32();
			header.InvasionType = reader.ReadInt32();
			header.InvasionPointX = reader.ReadDouble();

			if (version >= 118)
				header.SlimeRainTime = reader.ReadDouble();
			if (version >= 113)
				header.SundialCooldown = (int)reader.ReadByte();

			header.OreTiers = new int[3];
			header.Styles = new byte[8];
			if (version >= 66)
			{
				header.IsRaining = reader.ReadBoolean();
				header.RainTime = reader.ReadInt32();
				header.MaxRain = reader.ReadSingle();

				for (i = 0; i < 3; i++)
				{
					header.OreTiers[i] = reader.ReadInt32();
				}

				for (i = 0; i < 8; i++)
				{
					header.Styles[i] = reader.ReadByte();
				}

				header.CloudsActive = reader.ReadInt32();
				header.NumClouds = reader.ReadInt16();
				header.WindSpeed = reader.ReadSingle();


				List<string> anglerWhoFinishedToday = new List<string>();
				for (int index = reader.ReadInt32(); index > 0; --index)
				{
					//anglerWhoFinishedToday.Add(reader.ReadString());
				}
				if (version < 99)
					return;
				var savedAngler = reader.ReadBoolean();
				if (version < 101)
					return;
				var anglerQuest = reader.ReadInt32();



				//Saved Stylist
				reader.ReadBoolean();
				//Saved Tax Collector
				reader.ReadBoolean();

				if (version >= 201)
				{
					header.SavedGolfer = reader.ReadBoolean();
				}

				//Inavsion Size Start
				reader.ReadInt32();

				//temp cultist delay
				reader.ReadInt32();

				int killCountTotal = (int)reader.ReadInt16();
				for (int killIndex = 0; killIndex < killCountTotal; killIndex++)
				{
					reader.ReadInt32();
				}

				//fast forward time
				reader.ReadBoolean();


				//downed fishron
				reader.ReadBoolean();

				// Martians
				reader.ReadBoolean();

				// Cultists
				reader.ReadBoolean();

				// Moonlord
				reader.ReadBoolean();

				// Halloween King
				reader.ReadBoolean();

				// Halloween Tree
				reader.ReadBoolean();

				// Christmas Ice Queen
				reader.ReadBoolean();

				// Christmas Santank
				reader.ReadBoolean();

				// Christmas Tree
				reader.ReadBoolean();

				// Tower Solar
				reader.ReadBoolean();

				// Tower Vortex
				reader.ReadBoolean();

				// Tower Nebula
				reader.ReadBoolean();

				// Tower Stardust
				reader.ReadBoolean();

				// Tower Active Solar
				reader.ReadBoolean();

				// Tower Active Vortex
				reader.ReadBoolean();

				// Tower Active Nebula
				reader.ReadBoolean();

				// Tower Active Stardust
				reader.ReadBoolean();

				// Lunar Apolocyplse is up
				reader.ReadBoolean();

				reader.ReadBoolean();
				reader.ReadBoolean();
				reader.ReadInt32();
				int num2 = reader.ReadInt32();

				for (int index = 0; index < num2; ++index)
					reader.ReadInt32();

				reader.ReadBoolean();
				reader.ReadInt32();
				reader.ReadSingle();
				reader.ReadSingle();

				//DD2
				//NPC.savedBartender = 
				reader.ReadBoolean();
				//DD2Event.DownedInvasionT1 = 
				reader.ReadBoolean();
				//DD2Event.DownedInvasionT2 = 
				reader.ReadBoolean();
				//DD2Event.DownedInvasionT3 = 
				reader.ReadBoolean();

				// 1.4 Journey's End
				if (version > 194)
				{
					header.MushroomBg = reader.ReadByte();
				}

				if (version >= 215)
				{
					header.UnderworldBg = reader.ReadByte();
				}

				if (version >= 195)
				{
					header.BgTree2 = reader.ReadByte();
					header.BgTree3 = reader.ReadByte();
					header.BgTree4 = reader.ReadByte();
				}

				if (version >= 204)
				{
					header.CombatBookUsed = reader.ReadBoolean();
				}

				if (version >= 207)
				{
					header.TempLanternNightCooldown = reader.ReadInt32();
					header.TempLanternNightGenuine = reader.ReadBoolean();
					header.TempLanternNightManual = reader.ReadBoolean();
					header.TempLanternNightNextNightIsGenuine = reader.ReadBoolean();
				}

				// tree tops
				if (version >= 211)
				{
					// header.TreeTopVariations[0] = reader.ReadInt32();
					// header.TreeTopVariations[1] = reader.ReadInt32();
					// header.TreeTopVariations[2] = reader.ReadInt32();
					// header.TreeTopVariations[3] = reader.ReadInt32();
					// header.TreeTopVariations[4] = reader.ReadInt32();
					// header.TreeTopVariations[5] = reader.ReadInt32();
					// header.TreeTopVariations[6] = reader.ReadInt32();
					// header.TreeTopVariations[7] = reader.ReadInt32();
					// header.TreeTopVariations[8] = reader.ReadInt32();
					// header.TreeTopVariations[9] = reader.ReadInt32();
					// header.TreeTopVariations[10] = reader.ReadInt32();
					// header.TreeTopVariations[11] = reader.ReadInt32();
					// header.TreeTopVariations[12] = reader.ReadInt32();
				}

				if (version >= 212)
				{
					header.ForceHalloweenForToday = reader.ReadBoolean();
					header.ForceXMasForToday = reader.ReadBoolean();
				}

				if (version >= 216)
				{
					header.SavedOreTiersCopper = reader.ReadInt32();
					header.SavedOreTiersIron = reader.ReadInt32();
					header.SavedOreTiersSilver = reader.ReadInt32();
					header.SavedOreTiersGold = reader.ReadInt32();
				}
				else
				{
					header.SavedOreTiersCopper = -1;
					header.SavedOreTiersIron = -1;
					header.SavedOreTiersSilver = -1;
					header.SavedOreTiersGold = -1;
				}

				if (version >= 217)
				{
					header.BoughtCat = reader.ReadBoolean();
					header.BoughtDog = reader.ReadBoolean();
					header.BoughtBunny = reader.ReadBoolean();
				}

				if (version >= 223)
				{
					header.DownedEmpressOfLight = reader.ReadBoolean();
					header.DownedQueenSlime = reader.ReadBoolean();
				}

				if (version >= 240)
				{
					header.DownedDeerclops = reader.ReadBoolean();
				}
			}

			// pos 1903
			posTiles = stream.Position;
			progressPosition = stream.Position;


		}

		private void ReadWorldTiles()
		{
			Boolean theB;
			Byte theI;
			Tile theTile;
			Int32 i, j;

			if (bw != null)
				bw.ReportProgress((Int32)(((Single)progressPosition / stream.Length) * readWorldPerc)
					, "Reading Tiles");

			tiles = new Tile[MaxX, MaxY];

			for (i = 0; i < MaxX; i++)
			{
				for (j = 0; j < MaxY; j++)
				{
					theTile = new Tile();

					theB = reader.ReadBoolean();

					theTile.Active = theB;
					if (theB == true)
					{
						theI = reader.ReadByte();

						if (theI > 85)
							theTile.TileType = theI;

						theTile.TileType = theI;

						if (tileImportant[theI] == true)
						{
							theTile.Important = true;
							theTile.Frame = new PointInt16(reader.ReadInt16(), reader.ReadInt16());
						}
						else
							theTile.Important = false;
					}

					// /dev/null the Lighted Flag
					reader.ReadBoolean();

					theB = reader.ReadBoolean();

					theTile.Wall = theB;
					if (theB == true)
						theTile.WallType = reader.ReadByte();

					if (theTile.WallType == 0 && theTile.Wall == true)
						theTile.Wall = true;

					theB = reader.ReadBoolean();

					if (theB == true)
					{
						theTile.LiquidLevel = reader.ReadByte();
						theTile.Lava = reader.ReadBoolean();
					}

					tiles[i, j] = theTile;
				}
				progressPosition = stream.Position;
			}

			posChests = stream.Position;
		}

		private void ReadChests()
		{
			Int16 itemCount;
			Chest theChest = null;
			Item theItem;
			Int32 i, j;
			Int32 maxChests;
			int maxItems;
			chests = new List<Chest>();

			if (bw != null)
				bw.ReportProgress((Int32)(((Single)progressPosition / stream.Length) * readWorldPerc)
					, "Reading Chests");

			maxChests = reader.ReadInt16();
			maxItems = reader.ReadInt16();

			// System.Windows.Forms.MessageBox.Show(maxChests.ToString());

			for (i = 0; i < maxChests; i++)
			{

				theChest = new Chest();
				theChest.ChestId = i;
				theChest.Active = true;

				theChest.Coordinates = new Point(reader.ReadInt32(), reader.ReadInt32());

				// Console.WriteLine(theChest.Coordinates.ToString());

				theChest.Name = reader.ReadString();

				if (chestTypeList != null)
				{
					// System.Windows.Forms.MessageBox.Show("");
					if (chestTypeList.ContainsKey(theChest.Coordinates))
					{
						theChest.Type = chestTypeList[theChest.Coordinates];
						//System.Windows.Forms.MessageBox.Show(theChest.Type.ToString());
					}
				}
				else
				{
					theChest.Type = ChestType.Chest;
					//System.Windows.Forms.MessageBox.Show(theChest.Type.ToString());
				}

				for (j = 0; j < maxItems; j++)
				{
					if (header.ReleaseNumber > 68)
					{
						itemCount = reader.ReadInt16();
					}
					else
					{
						itemCount = reader.ReadByte();
					}

					if (itemCount > 0)
					{
						theItem = new Item();
						theItem.Id = j;
						theItem.Count = itemCount;

						if (header.ReleaseNumber >= 0x26)
						{
							theItem.Name = Global.Instance.Info.GetItemName(reader.ReadInt32());
							// System.Windows.Forms.MessageBox.Show(theItem.Name.ToString());
						}
						else
						{
							theItem.Name = reader.ReadString();
							//System.Windows.Forms.MessageBox.Show(theItem.Name.ToString());
						}

						if (header.ReleaseNumber >= 0x24)
							theItem.Prefix = reader.ReadByte();

						theChest.Items.Add(theItem);
					}
				}
				chests.Add(theChest);

				progressPosition = stream.Position;

			}

			posSigns = stream.Position;
		}

		private void ReadSigns()
		{

			Sign theSign;
			signs = new List<Sign>(1000);

			if (bw != null)
				bw.ReportProgress((Int32)(((Single)progressPosition / stream.Length) * readWorldPerc)
					, "Reading Signs");

			int maxSigns = reader.ReadInt16();

			for (Int32 i = 0; i < maxSigns; i++)
			{
				//isSign = reader.ReadBoolean();

				theSign = new Sign();
				theSign.Id = i;
				theSign.Active = true;

				theSign.Text = reader.ReadString();
				theSign.Position = new Point(reader.ReadInt32(), reader.ReadInt32());

				signs.Add(theSign);


				progressPosition = stream.Position;
			}

			posNpcs = stream.Position;

		}

		private void ReadNPCs()
		{
			Boolean nextNPC;
			NPC theNPC;
			NPCType npcType;
			Int32 i;

			npcs = new List<NPC>(20);
			i = 0;

			if (bw != null)
				bw.ReportProgress((Int32)(((Single)progressPosition / stream.Length) * readWorldPerc)
					, "Reading NPCs");

			nextNPC = reader.ReadBoolean();
			while (nextNPC == true)
			{
				theNPC = new NPC();

				theNPC.Id = i;

				theNPC.Active = nextNPC;

				theNPC.Type = (NPCType)reader.ReadInt32();


				//            if (Enum.TryParse(reader.ReadString().Replace(" ", ""), true, out npcType))
				//	theNPC.Type = npcType;
				//else
				//	theNPC.Type = NPCType.Unknown;

				theNPC.Name = reader.ReadString();
				theNPC.Position = new PointSingle(reader.ReadSingle(), reader.ReadSingle());
				theNPC.Homeless = reader.ReadBoolean();
				theNPC.HomeTile = new Point(reader.ReadInt32(), reader.ReadInt32());


				npcs.Add(theNPC);
				i++;

				nextNPC = reader.ReadBoolean();

				progressPosition = stream.Position;
			}

			posFooter = stream.Position;

		}

		private void ReadNPCNames()
		{
			if (header.ReleaseNumber >= 0x24)
			{
				header.MerchantsName = reader.ReadString();
				header.NursesName = reader.ReadString();
				header.ArmsDealersName = reader.ReadString();
				header.DryadsName = reader.ReadString();
				header.GuidesName = reader.ReadString();
				header.ClothiersName = reader.ReadString();
				header.DemolitionistsName = reader.ReadString();
				header.TinkerersName = reader.ReadString();
				header.WizardsName = reader.ReadString();
				header.MechanicsName = reader.ReadString();
				header.TrufflesName = reader.ReadString();
				header.SteamPunkersName = reader.ReadString();
				header.DyeTradersName = reader.ReadString();
				header.PartyGirlsName = reader.ReadString();
				header.CyborgsName = reader.ReadString();
				header.PaintersName = reader.ReadString();
				header.WitchDoctorsName = reader.ReadString();
				header.PiratesName = reader.ReadString();
			}
			else
			{
				header.MerchantsName = "Not set";
				header.NursesName = "Not Set";
				header.ArmsDealersName = "Not Set";
				header.DryadsName = "Not Set";
				header.GuidesName = "Not Set";
				header.ClothiersName = "Not Set";
				header.DemolitionistsName = "Not Set";
				header.TinkerersName = "Not Set";
				header.WizardsName = "Not Set";
				header.MechanicsName = "Not Set";
			}
		}

		private void ReadFooter()
		{
			footer = new Footer();

			if (bw != null)
				bw.ReportProgress((Int32)(((Single)progressPosition / stream.Length) * readWorldPerc)
					, "Reading Footer");

			footer.Active = reader.ReadBoolean();
			footer.Name = reader.ReadString();
			footer.Id = reader.ReadInt32();

			posEnd = stream.Position;
			progressPosition = stream.Position;

		}

		public int[,] ReadAndProcessWorld(String worldPath, BackgroundWorker worker)
		{

			Int32[,] retTiles;
			byte wallType;
			Timer t = null;




			if (worker != null)
			{
				bw = worker;
				progressPosition = 0;
				t = new Timer(333);
				t.Elapsed += new ElapsedEventHandler(timer_ReadWorld);
				t.Start();
			}

			if (SettingsManager.Instance.ShowChestTypes == true)
				chestTypeList = new Dictionary<Point, ChestType>();
			else
				chestTypeList = null;

			readWorldPerc = 45;

			stream = new FileStream(worldPath, FileMode.Open, FileAccess.Read);
			reader = new BinaryReader(stream, Encoding.Default);




			ReadHeader();
			// System.Windows.Forms.MessageBox.Show(reader.BaseStream.Position.ToString());
			// reader.BaseStream.Position += 1363;

			MaxX = header.MaxTiles.X;
			MaxY = header.MaxTiles.Y;

			// Reset MapTile List
			retTiles = new int[MaxX, MaxY];

			// WorldFile wf = new WorldFile();

			if (bw != null)
				bw.ReportProgress(0, "Reading and Processing Tiles");

			byte firstHeader, secondHeader, thirdHeader = 0;
			int ntileType = TileProperties.BackgroundOffset;

			// 1.4 Fix
			if (version > 194)
			{
				reader.BaseStream.Seek(sectionPointers[1], SeekOrigin.Begin);
			}

			// System.Windows.Forms.MessageBox.Show(reader.BaseStream.Position.ToString());
			// reader.BaseStream.Position += 1363;

			int run = 0;
			chestTypeList = new Dictionary<Point, ChestType>();
			for (int ncolumn = 0; ncolumn < header.MaxTiles.X; ncolumn++)
			{
				for (int nrow = 0; nrow < header.MaxTiles.Y; nrow++)
				{
					if (run > 0)
					{
						retTiles[ncolumn, nrow] = ntileType;
						run--;
						continue;
					}


					if (nrow < header.SurfaceLevel)
						ntileType = TileProperties.BackgroundOffset;
					else if (nrow == header.SurfaceLevel)
						ntileType = (Int16)(TileProperties.BackgroundOffset + 1); // Dirt Transition
					else if (nrow < (header.RockLayer + 38))
						ntileType = (Int16)(TileProperties.BackgroundOffset + 2); // Dirt
					else if (nrow == (header.RockLayer + 38))
						ntileType = (Int16)(TileProperties.BackgroundOffset + 4); // Rock Transition
					else if (nrow < (header.MaxTiles.Y - 202))
						ntileType = (Int16)(TileProperties.BackgroundOffset + 3); // Rock 
					else if (nrow == (header.MaxTiles.Y - 202))
						ntileType = (Int16)(TileProperties.BackgroundOffset + 6); // Underworld Transition
					else
						ntileType = (Int16)(TileProperties.BackgroundOffset + 5); // Underworld

					secondHeader = 0;
					thirdHeader = 0;

					firstHeader = reader.ReadByte();
					if ((firstHeader & 1) == 1)
					{
						secondHeader = reader.ReadByte();
						if ((secondHeader & 1) == 1)
							thirdHeader = reader.ReadByte();
					}

					if ((firstHeader & 2) == 2)
					{
						if ((firstHeader & 32) == 32)
						{
							byte num5 = reader.ReadByte();
							ntileType = reader.ReadByte() << 8 | num5;
						}
						else
							ntileType = reader.ReadByte();

						if (tileImportant[ntileType])
						{
							var typeX = reader.ReadInt16();
							var typeY = reader.ReadInt16();
							if (ntileType == TileProperties.ExposedGems)
							{
								if (typeX == 0)
									ntileType = TileProperties.Amethyst;
								else if (typeX == 18)
									ntileType = TileProperties.Topaz;
								else if (typeX == 36)
									ntileType = TileProperties.Sapphire;
								else if (typeX == 54)
									ntileType = TileProperties.Emerald;
								else if (typeX == 72)
									ntileType = TileProperties.Ruby;
								else if (typeX == 90)
									ntileType = TileProperties.Diamond;
								else if (typeX == 108)
									ntileType = TileProperties.ExposedGems;
								// If it's 108 we keep it as ExposedGems so it get the Amber marker.
							}
							else if (ntileType == TileProperties.SmallDetritus)
							{
								if ((typeX % 36 == 0) && (typeY == 18))
								{
									int type = typeX / 36;

									if (type == 16)
										ntileType = TileProperties.CopperCache;
									else if (type == 17)
										ntileType = TileProperties.SilverCache;
									else if (type == 18)
										ntileType = TileProperties.GoldCache;
									else if (type == 19)
										ntileType = TileProperties.Amethyst;
									else if (type == 20)
										ntileType = TileProperties.Topaz;
									else if (type == 21)
										ntileType = TileProperties.Sapphire;
									else if (type == 22)
										ntileType = TileProperties.Emerald;
									else if (type == 23)
										ntileType = TileProperties.Ruby;
									else if (type == 24)
										ntileType = TileProperties.Diamond;
								}
							}
							else if (ntileType == TileProperties.LargeDetritus)
							{
								if ((typeX % 54 == 0) && (typeY == 0))
								{
									int type = typeX / 54;

									if (type == 16 || type == 17)
										ntileType = TileProperties.CopperCache;
									else if (type == 18 || type == 19)
										ntileType = TileProperties.SilverCache;
									else if (type == 20 || type == 21)
										ntileType = TileProperties.GoldCache;
								}
							}
							else if (ntileType == TileProperties.LargeDetritus2)
							{
								if ((typeX % 54 == 0) && (typeY == 0))
								{
									int type = typeX / 54;

									if (type == 17)
										ntileType = TileProperties.EnchantedSword;
								}
							}
							if ((ntileType == TileProperties.Chest) && (typeX % 36 == 0) && (typeY == 0))
							{
								if ((typeX / 36) <= (Int32)ChestType.LockedFrozenChest)
								{
									chestTypeList.Add(new Point(ncolumn, nrow), (ChestType)(typeX / 36));
								}
								else
								{
									chestTypeList.Add(new Point(ncolumn, nrow), ChestType.Unknown);
								}
							}

						}
						if ((thirdHeader & 8) == 8)
							reader.ReadByte();

					}
					if ((firstHeader & 4) == 4)
					{
						wallType = reader.ReadByte();
						if (ntileType >= TileProperties.Unknown)
							ntileType = (Int16)(wallType + TileProperties.WallOffset);
						if ((thirdHeader & 16) == 16)
							reader.ReadByte();
					}
					if ((firstHeader & 8) == 8)
					{
						ntileType = TileProperties.Water;
						reader.ReadByte();

					}
					else if ((firstHeader & 16) == 16)
					{
						ntileType = TileProperties.Lava;
						reader.ReadByte();

					}

					if ((firstHeader & 64) == 64)
					{
						run = reader.ReadByte();

					}
					if ((firstHeader & 128) == 128)
						run = reader.ReadInt16();



					retTiles[ncolumn, nrow] = ntileType;

				}

			}

			foreach (var point in chestTypeList)
			{
				// Console.WriteLine(point.Key.ToString());
				// System.Windows.Forms.MessageBox.Show(point.Key.ToString());
			}



			// reader.BaseStream.Seek(sectionPointers[0], SeekOrigin.Begin);


			// System.Windows.Forms.MessageBox.Show(reader.BaseStream.Position.ToString());

			// reader.BaseStream.Position += 42;

			ReadChests();
			ReadSigns();
			ReadNPCs();
			//ReadNPCNames();
			ReadFooter();

			// Save Public Flags
			SpawnpointGlobal = header.SpawnPoint;

			if (bw != null)
			{
				t.Stop();
				bw = null;
			}

			reader.Close();

			return retTiles;
		}

		private void ScanPastWorldTiles()
		{
			Boolean theB;
			Byte theI;
			Int32 i, j;
			Int16 RLE = 0;

			if (bw != null)
				bw.ReportProgress((Int32)(((Single)progressPosition / stream.Length) * readWorldPerc)
					, "Skipping Tiles");

			if (header.ReleaseNumber < 0x24)
			{
				for (i = 0; i < MaxX; i++)
				{
					for (j = 0; j < MaxY; j++)
					{
						theB = reader.ReadBoolean();

						if (theB == true)
						{
							theI = reader.ReadByte();

							if (tileImportant[theI] == true)
							{
								reader.ReadInt16();
								reader.ReadInt16();
							}
						}

						reader.ReadBoolean();

						theB = reader.ReadBoolean();

						if (theB == true)
							reader.ReadByte();

						theB = reader.ReadBoolean();

						if (theB == true)
						{
							reader.ReadByte();
							reader.ReadBoolean();
						}
					}
					progressPosition = stream.Position;
				}
			}
			else
			{
				for (i = 0; i < MaxX; i++)
				{
					for (j = 0; j < MaxY; j++)
					{
						if (RLE == 0)
						{
							theB = reader.ReadBoolean();

							if (theB == true)
							{
								theI = reader.ReadByte();

								if (tileImportant[theI] == true)
								{
									reader.ReadInt16();
									reader.ReadInt16();
								}
							}

							theB = reader.ReadBoolean();

							if (theB == true)
								reader.ReadByte();

							theB = reader.ReadBoolean();

							if (theB == true)
							{
								reader.ReadByte();
								reader.ReadBoolean();
							}

							reader.ReadBoolean();

							RLE = reader.ReadInt16();
						}
						else
						{
							RLE--;
						}
					}
					progressPosition = stream.Position;
				}
			}
			posChests = stream.Position;
		}


		// This is used to get only the chests from the file.  For the LoadInformation button.
		public List<Chest> GetChests(String world, BackgroundWorker worker = null)
		{
			Timer t = null;
			if (worker != null)
			{
				bw = worker;
				t = new Timer(333);
				t.Elapsed += new ElapsedEventHandler(timer_ReadWorld);
				t.Start();
				progressPosition = 0;
			}

			readWorldPerc = 100;

			stream = new FileStream(world, FileMode.Open, FileAccess.Read);
			reader = new BinaryReader(stream);

#if (DEBUG == false)
			try
			{
#endif
			ReadHeader();

			posChests = new BackwardsScanner(stream, header).SeekToChestsBackwards();

			if (posChests != 0)
			{
				ReadChests();
			}
			else
			{
				stream.Seek(posTiles, SeekOrigin.Begin);
				ScanPastWorldTiles();
				ReadChests();
			}
#if (DEBUG == false)
			}
			catch (Exception e)
			{
				if (bw != null)
				{
					t.Stop();
					bw = null;
				}
				throw e;
			}
#endif

			reader.Close();


			if (bw != null)
			{
				t.Stop();
				bw = null;
			}

			reader.Close();

			return chests;
		}
		#endregion

		#region SaveFunctions
		public void SaveWorld(String world)
		{
			FileStream stream = new FileStream(world, FileMode.Create, FileAccess.Write);
			BinaryWriter writer = new BinaryWriter(stream);

			SaveHeader(writer);
			SaveWorldTiles(writer);
			SaveChests(writer);
			SaveSigns(writer);
			SaveNPCs(writer);
			SaveFooter(writer);

			writer.Close();
		}

		private void SaveHeader(BinaryWriter writer)
		{
			writer.Write(header.ReleaseNumber);
			writer.Write(header.Name);
			writer.Write(header.Id);
			writer.Write(header.WorldCoords.TopLeft.X);
			writer.Write(header.WorldCoords.TopLeft.Y);
			writer.Write(header.WorldCoords.BottomRight.X);
			writer.Write(header.WorldCoords.BottomRight.Y);
			writer.Write(header.MaxTiles.Y);
			writer.Write(header.MaxTiles.X);
			writer.Write(header.SpawnPoint.X);
			writer.Write(header.SpawnPoint.Y);
			writer.Write(header.SurfaceLevel);
			writer.Write(header.RockLayer);
			writer.Write(header.TemporaryTime);
			writer.Write(header.IsDayTime);
			writer.Write(header.MoonPhase);
			writer.Write(header.IsBloodMoon);
			writer.Write(header.DungeonPoint.X);
			writer.Write(header.DungeonPoint.Y);
			writer.Write(header.IsBoss1Dead);
			writer.Write(header.IsBoss2Dead);
			writer.Write(header.IsBoss3Dead);
			writer.Write(header.IsShadowOrbSmashed);
			writer.Write(header.IsMeteorSpawned);
			writer.Write(header.ShadowOrbsSmashed);
			writer.Write(header.InvasionDelay);
			writer.Write(header.InvasionSize);
			writer.Write(header.InvasionType);
			writer.Write(header.InvasionPointX);
		}

		private void SaveWorldTiles(BinaryWriter writer)
		{
			Tile theTile;
			Int32 i, j;

			for (i = 0; i < MaxX; i++)
			{
				for (j = 0; j < MaxY; j++)
				{
					theTile = tiles[i, j];


					writer.Write(theTile.Active);

					if (theTile.Active)
					{
						writer.Write(theTile.TileType);

						if (tileImportant[theTile.TileType] == true)
						{
							writer.Write(theTile.Frame.X);
							writer.Write(theTile.Frame.Y);
						}
					}

					writer.Write(theTile.Wall);

					if (theTile.Wall)
						writer.Write(theTile.WallType);

					if (theTile.LiquidLevel > 0)
					{
						writer.Write(true);
						writer.Write(theTile.LiquidLevel);
						writer.Write(theTile.Lava);
					}
					else
					{
						writer.Write(false);
					}
				}
			}
		}

		private void SaveChests(BinaryWriter writer)
		{
			Chest nextChest;
			Item nextItem;
			Int32 i, j;

			List<Item>.Enumerator iEnum;
			List<Chest>.Enumerator cEnum;

			cEnum = chests.GetEnumerator();

			cEnum.MoveNext();
			nextChest = cEnum.Current;

			for (i = 0; i < 8000; i++)
			{
				if (nextChest != null && i == nextChest.ChestId)
				{
					writer.Write(nextChest.Active);

					writer.Write(nextChest.Coordinates.X);
					writer.Write(nextChest.Coordinates.Y);

					iEnum = nextChest.Items.GetEnumerator();

					iEnum.MoveNext();
					nextItem = iEnum.Current;

					for (j = 0; j < 20; j++)
					{
						if (nextItem != null && j == nextItem.Id)
						{
							writer.Write(nextItem.Count);
							writer.Write(nextItem.Name);

							iEnum.MoveNext();
							nextItem = iEnum.Current;
						}
						else
						{
							writer.Write((Int16)0);
						}
					}

					cEnum.MoveNext();
					nextChest = cEnum.Current;
				}
				else
				{
					writer.Write(false);
				}
			}
		}

		private void SaveSigns(BinaryWriter writer)
		{
			Sign theSign;
			Int32 i;

			for (i = 0; i < 1000; i++)
			{
				theSign = signs[i];

				writer.Write(theSign.Active);

				if (theSign.Active == true)
				{
					writer.Write(theSign.Text);
					writer.Write(theSign.Position.X);
					writer.Write(theSign.Position.Y);
				}
			}
		}

		private void SaveNPCs(BinaryWriter writer)
		{
			Int32 i = 0;
			NPC theNPC = npcs[i];

			while (theNPC != null)
			{
				writer.Write(theNPC.Active);
				writer.Write(theNPC.Name);
				writer.Write(theNPC.Position.X);
				writer.Write(theNPC.Position.Y);
				writer.Write(theNPC.Homeless);
				writer.Write(theNPC.HomeTile.X);
				writer.Write(theNPC.HomeTile.Y);

				i++;
				theNPC = npcs[i];
			}
			writer.Write(false);
		}

		private void SaveFooter(BinaryWriter writer)
		{
			writer.Write(footer.Active);
			writer.Write(footer.Name);
			writer.Write(footer.Id);
		}
		#endregion

		#region ScanningFunctions
		// Helper function to help parse through the world loading a tile run at a time.
		// Lots of bounds checking to make sure we catch when the first errors happen.
		// Designed for 1.2.0.1
		public bool SanityCheckWorld(String world)
		{
			String error;
			int strictbool;
			int i, j;
			String byteStringOld;
			byte[] byteStreamOld;
			int byteStreamOldLength;
			String byteString;
			byte[] byteStream;
			int byteStreamPos;
			Tile curTile = new Tile();
			int RLEValue;
			byteStream = new byte[40];
			byteStreamPos = 0;
			byteStreamOld = new byte[40];
			byteStreamOldLength = 0;
			Int32 tilesRead = 0;
			stream = new FileStream(world, FileMode.Open, FileAccess.Read);
			reader = new BinaryReader(stream);

			ReadHeader();

			RLEValue = 0;
			for (i = 0; i < MaxX; i++)
			{
				for (j = 0; j < MaxY; j++)
				{
					if (RLEValue == 0)
					{
						if (tilesRead == 1773)
							tilesRead = 1773;

						byteStreamPos = 0;
						curTile.Reset();
						strictbool = reader.ReadByte();
						byteStream[byteStreamPos] = (byte)strictbool;
						byteStreamPos++;

						if (strictbool > 1)
						{
							error = String.Format("Failed on the Activate Boolean read: 0x{1:X2}", (byte)strictbool);
						}

						curTile.Active = (strictbool == 0) ? false : true;

						if (curTile.Active)
						{
							curTile.TileType = reader.ReadByte();
							byteStream[byteStreamPos] = curTile.TileType;
							byteStreamPos++;

							if (curTile.TileType >= TileProperties.Unknown)
							{
								error = String.Format("Failed on the TileType Byte read: 0x{1:X2}", curTile.TileType);
							}

							if (Global.Instance.Info.Tiles[(int)curTile.TileType].colorName == "FindImportant")
							{
								error = String.Format("TileType {0} has unknown importance.", curTile.TileType);
							}

							if (TileProperties.tileTypeDefs[curTile.TileType].IsImportant)
							{
								curTile.Important = true;

								PointInt16 p = curTile.Frame;
								p.X = reader.ReadInt16();
								byteStream[byteStreamPos] = (byte)(p.X & 0xFF);
								byteStreamPos++;
								byteStream[byteStreamPos] = (byte)((p.X & 0xFF00) >> 8);
								byteStreamPos++;

								p.Y = reader.ReadInt16();
								byteStream[byteStreamPos] = (byte)(p.Y & 0xFF);
								byteStreamPos++;
								byteStream[byteStreamPos] = (byte)((p.Y & 0xFF00) >> 8);
								byteStreamPos++;

								curTile.Frame = p;
							}
							else
							{
								curTile.Important = false;
							}

							strictbool = reader.ReadByte();
							byteStream[byteStreamPos] = (byte)strictbool;
							byteStreamPos++;

							if (strictbool > 1)
							{
								error = String.Format("Failed on the Tile Color Boolean read: 0x{0:X2}", (byte)strictbool);
							}

							if (strictbool >= 1)
							{
								curTile.TileColor = reader.ReadByte();
								byteStream[byteStreamPos] = curTile.TileColor;
								byteStreamPos++;

								if (curTile.TileColor == 0)
								{
									error = String.Format("Failed on the Tile Color Byte read: 0x{0:X2}", curTile.TileColor);
								}
							}
						}

						strictbool = reader.ReadByte();
						byteStream[byteStreamPos] = (byte)(strictbool);
						byteStreamPos++;

						if (strictbool > 1)
						{
							error = String.Format("Failed in the Wall Active Boolean read: 0x{0:X2}", (byte)strictbool);
						}

						if (strictbool >= 1)
						{
							curTile.WallType = reader.ReadByte();
							byteStream[byteStreamPos] = curTile.WallType;
							byteStreamPos++;

							if (curTile.WallType == 0 || curTile.WallType > Global.Instance.Info.Walls.Count)
							{
								error = String.Format("Failed in the Wall Type Byte read: 0x{0:X2}", curTile.WallType);
							}

							strictbool = reader.ReadByte();
							byteStream[byteStreamPos] = (byte)(strictbool);
							byteStreamPos++;

							if (strictbool > 1)
							{
								error = String.Format("Failed in the Wall Color Boolean read: 0x{0:X2}", (byte)strictbool);
							}

							if (strictbool >= 1)
							{
								curTile.WallColor = reader.ReadByte();
								byteStream[byteStreamPos] = curTile.WallColor;
								byteStreamPos++;

								if (curTile.WallColor == 0)
								{
									error = String.Format("Failed in the Wall Color Byte read: 0x{0:X2}", (byte)strictbool);
								}
							}
						}

						strictbool = reader.ReadByte();
						byteStream[byteStreamPos] = (byte)(strictbool);
						byteStreamPos++;

						if (strictbool > 1)
						{
							error = String.Format("Failed in the Liquid Active Boolean read: 0x{0:X2}", (byte)strictbool);
						}

						if (strictbool >= 1)
						{
							curTile.LiquidLevel = reader.ReadByte();
							byteStream[byteStreamPos] = curTile.LiquidLevel;
							byteStreamPos++;

							strictbool = reader.ReadByte();
							byteStream[byteStreamPos] = (byte)(strictbool);
							byteStreamPos++;

							if (strictbool > 1)
							{
								error = String.Format("Failed in the IsLava Boolean read: 0x{0:X2}", (byte)strictbool);
							}

							curTile.Lava = (strictbool == 0) ? false : true;

							strictbool = reader.ReadByte();
							byteStream[byteStreamPos] = (byte)(strictbool);
							byteStreamPos++;

							if (strictbool > 1)
							{
								error = String.Format("Failed in the IsHoney Boolean read: 0x{0:X2}", (byte)strictbool);
							}

							curTile.Honey = (strictbool == 0) ? false : true;
						}

						strictbool = reader.ReadByte();
						byteStream[byteStreamPos] = (byte)(strictbool);
						byteStreamPos++;

						if (strictbool > 1)
						{
							error = String.Format("Failed in the Red Wire Boolean read: 0x{0:X2}", (byte)strictbool);
						}

						curTile.RedWire = (strictbool == 0) ? false : true;

						strictbool = reader.ReadByte();
						byteStream[byteStreamPos] = (byte)(strictbool);
						byteStreamPos++;

						if (strictbool > 1)
						{
							error = String.Format("Failed in the Blue Wire Boolean read: 0x{0:X2}", (byte)strictbool);
						}

						curTile.BlueWire = (strictbool == 0) ? false : true;

						strictbool = reader.ReadByte();
						byteStream[byteStreamPos] = (byte)(strictbool);
						byteStreamPos++;

						if (strictbool > 1)
						{
							error = String.Format("Failed in the Halftile Boolean read: 0x{0:X2}", (byte)strictbool);
						}

						curTile.GreenWire = (strictbool == 0) ? false : true;

						strictbool = reader.ReadByte();
						byteStream[byteStreamPos] = (byte)(strictbool);
						byteStreamPos++;

						if (strictbool > 1)
						{
							error = String.Format("Failed in the Halftile Boolean read: 0x{0:X2}", (byte)strictbool);
						}

						curTile.Halftile = (strictbool == 0) ? false : true;

						curTile.Slope = reader.ReadByte();

						strictbool = reader.ReadByte();
						byteStream[byteStreamPos] = (byte)(strictbool);
						byteStreamPos++;

						if (strictbool > 1)
						{
							error = String.Format("Failed in the Actuator Boolean read: 0x{0:X2}", (byte)strictbool);
						}

						curTile.Actuator = (strictbool == 0) ? false : true;

						strictbool = reader.ReadByte();
						byteStream[byteStreamPos] = (byte)(strictbool);
						byteStreamPos++;

						if (strictbool > 1)
						{
							error = String.Format("Failed in the Inactive Boolean read: 0x{0:X2}", (byte)strictbool);
						}

						curTile.Inactive = (strictbool == 0) ? false : true;

						RLEValue = reader.ReadInt16();
						byteStream[byteStreamPos] = (byte)(RLEValue & 0xFF);
						byteStreamPos++;
						byteStream[byteStreamPos] = (byte)((RLEValue & 0xFF00) >> 8);
						byteStreamPos++;

						for (int k = byteStreamPos; k < byteStream.Length; k++)
							byteStream[k] = 0;

						for (int k = 0; k < byteStream.Length; k++)
							byteStreamOld[k] = byteStream[k];

						byteStreamOldLength = byteStreamPos;

						byteString = "";
						for (int k = 0; k < byteStreamPos; k++)
							byteString = byteString + String.Format("{0:X2} ", byteStream[k]);

						byteStringOld = byteString;
						tilesRead++;
					}
					else
					{
						RLEValue--;
						continue;
					}
				}
			}
			ReadChests();
			ReadSigns();
			ReadNPCs();
			ReadNPCNames();
			ReadFooter();
			return true;
		}

		// Ok so this is how this works.
		// It starts out by walking the file backwards to find the ending offset.
		// It then sets up the original path and starts scanning along the tiles.
		// When it finds an unknown tile type the program duplicates the current
		// path and makes one follow the path of it being non-important while
		// the other follows the path of being important.
		// When a path turns out not to fit the data it gets removed from the list.
		// All paths get checked in parallel.
		public TileImportance[] ScanWorld(String world, BackgroundWorker worker = null)
		{
			Timer t = null;
			TileImportance[] retList;

			if (worker != null)
			{
				bw = worker;
				t = new Timer(333);
				t.Elapsed += new ElapsedEventHandler(timer_ScanWorld);
				t.Enabled = true;
				t.Start();
			}

			stream = new FileStream(world, FileMode.Open, FileAccess.Read);
			reader = new BinaryReader(stream);
			buffReader = new BufferedBinaryReader(stream, 5000, 1000);

			posChests = new BackwardsScanner(stream, header).SeekToChestsBackwards();

			stream.Seek(0, SeekOrigin.Begin);
			ReadHeader();
			retList = ScanWorldTiles();

			if (bw != null)
			{
				t.Stop();
				bw = null;
			}

			reader.Close();

			return retList;
		}

		private void timer_ScanWorld(object sender, ElapsedEventArgs e)
		{
			bw.ReportProgress(progress);
		}

		private TileImportance[] ScanWorldTiles()
		{
			Byte tryByte;
			Byte tileType;
			Tile curTile;
			Int32 i, j, k;
			tileReader curReader;
			tileReader splitReader;
			TileImportance curList;
			TileImportance[] returnList;

			List<tileReader> readerList = new List<tileReader>();

			buffReader.Seek(stream.Position);

			// We set up the lookup we can change here and we'll simply change it.
			// We might not even need this lookup at all.  Work the logic later.
			curReader = new tileReader(stream.Position);
			curReader.startAt = new Point(0, 0);
			curReader.splitAt = 255;
			curList = curReader.tileLookup;
			curTile = curReader.tile;
			readerList.Add(curReader);

			for (i = 0; i < MaxX; i++)
			{
				progress = (Int32)(((i * MaxY) / (double)(MaxX * MaxY)) * 100);

				for (j = 0; j < MaxY; j++)
				{
					// If somehow we manage to knock out every reader as bad then we need to quit.
					if (readerList.Count == 0)
						return null;

					for (k = 0; k < readerList.Count; k++)
					{
						// No reason to keep resetting these if we only have one path going.
						if (readerList.Count > 1)
						{
							curReader = readerList[k];
							curList = curReader.tileLookup;
							curTile = curReader.tile;

							buffReader.Seek(curReader.filePos);
						}

						tryByte = buffReader.ReadByte();

						if (tryByte > 1)
							goto badPath;

						if (tryByte == 1)
						{
							curTile.Active = true;

							tileType = buffReader.ReadByte();
							curTile.TileType = tileType;

							if (curList.isKnown(tileType) == false)
							{
								// Here we need to split the lists.
								curReader.tileOrder.Add(tileType);
								curList.setKnown(tileType, true);
								splitReader = new tileReader(curReader);
								splitReader.startAt = new Point(i, j);
								splitReader.splitAt = tileType;
								curList.setImportant(tileType, false);
								splitReader.tileLookup.setImportant(tileType, true);
								readerList.Add(splitReader);
								bw.ReportProgress(progress, String.Format("Split #{0} {1}", splitReader.id, readerList.Count));

								curTile.Important = false;
							}
							else
							{
								curTile.Important = curList.isImportant(tileType);
							}
						}
						else
						{
							curTile.Active = false;
							curTile.Important = false;
						}

						if (curTile.Important == true)
						{
							curTile.Frame = new PointInt16(buffReader.ReadInt16(), buffReader.ReadInt16());
						}

						// isLighted
						tryByte = buffReader.ReadByte();

						if (tryByte > 1)
							goto badPath;

						// isWall
						tryByte = buffReader.ReadByte();

						if (tryByte > 1)
							goto badPath;

						if (tryByte == 1)
						{
							curTile.Wall = true;
							// wallType
							tryByte = buffReader.ReadByte();

							// It turns out there will never be a wall type 0.
							if (tryByte == 0)
								goto badPath;

							curTile.WallType = tryByte;
						}
						else
						{
							curTile.Wall = false;
						}

						// isWater
						tryByte = buffReader.ReadByte();

						if (tryByte > 1)
							goto badPath;

						if (tryByte == 1)
						{
							// waterLevel
							tryByte = buffReader.ReadByte();
							curTile.LiquidLevel = tryByte;

							// We can have any water value besides zero, if the isWater bit is set.
							if (tryByte == 0)
								goto badPath;

							// isLava
							tryByte = buffReader.ReadByte();

							if (tryByte > 1)
								goto badPath;

							if (tryByte == 1)
								curTile.Lava = true;
							else
								curTile.Lava = false;
						}

						curReader.filePos = buffReader.Position;

						// This path passed over the end of the tile range.  Bad path.
						// We might not always have a valid chest position though, if they
						// have changed something in a new version.
						if (posChests != 0 && curReader.filePos > posChests)
							goto badPath;

						curReader.tilesRead++;
						continue;

					badPath:
						bw.ReportProgress(progress, String.Format("Path #{0} Terminated {1}", readerList[k].id, readerList.Count - 1));

						// Erase the bad path from the list to process.
						readerList.RemoveAt(k);

						// Now that we removed one we need to move the loop back one to 
						// compensate for how it shifts them over.  Otherwise we could
						// have a 0,1,2 then remove 1 which leaves 0,1 and our next
						// loop is for 2 so the old 2 (new 1) gets skipped.
						k--;
						// If we took it back to one we'll need to set up for the next loop
						// as we put the skip in to speed it up.
						if (readerList.Count == 1)
						{
							curReader = readerList[0];
							curList = curReader.tileLookup;
							curTile = curReader.tile;

							buffReader.Seek(curReader.filePos);
						}
					}
				}
			}

			// Time to prep for the return.
			returnList = new TileImportance[readerList.Count];

			// Find the first one that matched up with the end of the tile position.
			for (k = 0; k < readerList.Count; k++)
			{
				if (readerList[k].filePos == posChests)
					break;
			}

			// Now generate the list but put the one that matched first, if one existed.
			if (readerList.Count == k)
			{
				for (i = 0; i < readerList.Count; i++)
					returnList[i] = readerList[i].tileLookup;
			}
			else
			{
				returnList[0] = readerList[k].tileLookup;

				j = 1;
				for (i = 0; i < readerList.Count; i++)
				{
					if (i != k)
					{
						returnList[j] = readerList[k].tileLookup;
						j++;
					}
				}
			}

			//bw.ReportProgress(progress, String.Format("Path #{0} Terminated {1}", readerList[k].id, readerList.Count - 1));

			return returnList;
		}
		#endregion

		#region GetSet Functions
		public WorldHeader Header
		{
			get
			{
				return header;
			}
			set
			{
				header = value;
			}
		}

		public Dictionary<Point, ChestType> ChestTypeList
		{
			get
			{
				return chestTypeList;
			}
			set
			{
				this.chestTypeList = value;
			}
		}



		public Tile[,] Tiles
		{
			get
			{
				return tiles;
			}
		}

		public List<Chest> Chests
		{
			get
			{
				return chests;
			}
			set
			{
				this.chests = value;
			}
		}

		public List<Sign> Signs
		{
			get
			{
				return signs;
			}
			set
			{
				this.signs = value;
			}
		}

		public List<NPC> Npcs
		{
			get
			{
				return npcs;
			}
			set
			{
				this.npcs = value;
			}
		}

		public Footer Footer
		{
			get
			{
				return footer;
			}
		}
		#endregion

		public String GetWorldName(String worldFile)
		{
			String worldName;
			String headerName;
			int headerId;

			try
			{
				stream = new FileStream(worldFile, FileMode.Open, FileAccess.Read);
				reader = new BinaryReader(stream);
				backReader = new BackwardsBinaryReader(stream);
			}
			catch (Exception e)
			{
				e.ToString();
				return "Error loading worldname";
			}

			// Skip the release number.
			reader.ReadInt32();
			headerName = reader.ReadString();
			headerId = reader.ReadInt32();

			if (CompareFooter(headerName, headerId) == true)
				worldName = headerName;
			else
				worldName = "Not a valid World file";

			reader.Close();

			return worldName;
		}

		private Boolean CompareFooter(String worldName, Int32 worldId)
		{
			Boolean returnVal = false;

			long position = this.stream.Position;

			this.stream.Seek(0, SeekOrigin.End);

#if (DEBUG == false)
			try
			{
#endif
			footer = new Footer();

			footer.Id = backReader.ReadBackwardsInt32();
			footer.Name = backReader.ReadBackwardsString();
			footer.Active = backReader.ReadBackwardsBoolean();

			if (footer.Active == true && footer.Name == worldName && footer.Id == worldId)
				returnVal = true;
#if (DEBUG == false)
			}
			catch (EndOfStreamException e)
			{
				// We don't need to do this but I do just so the compiler will not throw an warning
				// for never using it.
				e.GetType();

				// If we read past the end of the stream we know it did not match.
				returnVal = false;
			}
#endif
			// We set the position back to where it was when we were called.
			this.stream.Seek(position, SeekOrigin.Begin);
			return returnVal;
		}
	}
}