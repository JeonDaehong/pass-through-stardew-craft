using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace StardewCraft
{
    internal sealed class ModConfig
    {
        /// <summary>StarCraft: Remastered install; only its bundled maps are read.</summary>
        public string StarCraftDir { get; set; } = @"C:\Program Files (x86)\StarCraft";

        /// <summary>Empty means the default inside the mod folder (see scripts/install.sh).</summary>
        public string GuestPath { get; set; } = "";
        public string DataDir { get; set; } = "";
        public string BaseMap { get; set; } = "";
        public string Palette { get; set; } = "jungle";

        public SButton RaidKey { get; set; } = SButton.F5;
        public SButton ClearKey { get; set; } = SButton.F6;
        public SButton WeaponKitKey { get; set; } = SButton.F7;
        public SButton BuildTankKey { get; set; } = SButton.B;
        public SButton BuildAntiAirKey { get; set; } = SButton.N;

        public int SpawnDistanceTiles { get; set; } = 10;
        public int DaysPerNightStage { get; set; } = 3;
        public int MaxRaiders { get; set; } = 120;
        public bool NightRaids { get; set; } = true;
        public int NightRaidStartTime { get; set; } = 1900;

        public float WeaponDamageMultiplier { get; set; } = 1.5f;
        public float SlingshotDamageMultiplier { get; set; } = 2f;
        public bool ShowHealthBars { get; set; } = true;
        public bool TurretsCostCrops { get; set; } = false;

        public float CropRaiderChance { get; set; } = 0.4f;
        public bool AnimalsJoinFights { get; set; } = true;
        public float AnimalCounterChance { get; set; } = 0.35f;
        public bool AnimalsCanDie { get; set; } = true;
        public int SmallAnimalHp { get; set; } = 60;
        public int LargeAnimalHp { get; set; } = 150;
        public bool VillagersJoinFights { get; set; } = true;
        public int VillagerMinDamage { get; set; } = 8;
        public int VillagerMaxDamage { get; set; } = 18;

        public SButton ScreenshotKey { get; set; } = SButton.F8;
        public string FfmpegPath { get; set; } = "ffmpeg";
        public string ScreenshotDir { get; set; } = @"%USERPROFILE%\Pictures\StardewCraft";
    }

    /// <summary>Stardew side of the passthrough: owns the world and the farmer, draws what OpenBW simulates.</summary>
    internal sealed class ModEntry : Mod
    {
        private static readonly string[] WeaponKit =
        {
            "(W)4",   // Galaxy Sword
            "(W)29",  // Galaxy Hammer
            "(W)23",  // Galaxy Dagger
            "(W)34",  // Galaxy Slingshot
        };

        private ModConfig config = null!;
        private Shm? shm;
        private Process? guest;
        private BwGraphics? graphics;
        private Combat? combat;
        private Raids? raids;
        private Turrets? turrets;
        private Screenshotter? screenshotter;
        private Allies? allies;
        private CropRaiders? cropRaiders;
        private RaiderMemory? raiderMemory;

        private int mapId;
        private SimLocation? sim;
        private UnitInfo[] units = Array.Empty<UnitInfo>();
        private uint lastDamageTotal;
        private uint lastGuestStatus = uint.MaxValue;

        public override void Entry(IModHelper helper)
        {
            this.config = helper.ReadConfig<ModConfig>();

            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += (_, _) =>
            {
                this.turrets?.Load();
                this.LoadLocation(Game1.currentLocation);
            };
            helper.Events.GameLoop.Saving += (_, _) => this.turrets?.Save();
            helper.Events.GameLoop.DayStarted += (_, _) =>
            {
                this.allies?.ResetDay();
                this.raiderMemory?.Clear();
            };
            helper.Events.Player.Warped += (_, e) => { if (e.IsLocalPlayer) this.LoadLocation(e.NewLocation); };
            helper.Events.GameLoop.ReturnedToTitle += (_, _) =>
            {
                this.sim = null;
                this.raiderMemory?.Clear();
            };
            helper.Events.GameLoop.TimeChanged += this.OnTimeChanged;
            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            helper.Events.Display.RenderedWorld += this.OnRenderedWorld;
            helper.Events.Input.ButtonPressed += this.OnButtonPressed;
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                this.StopGuest();
            };
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            string modDir = this.Helper.DirectoryPath;
            if (string.IsNullOrWhiteSpace(this.config.GuestPath))
                this.config.GuestPath = Path.Combine(modDir, "guest", "stardewcraft_guest.exe");
            if (string.IsNullOrWhiteSpace(this.config.DataDir))
                this.config.DataDir = Path.Combine(modDir, "bw-data");
            if (string.IsNullOrWhiteSpace(this.config.BaseMap))
                this.config.BaseMap = Path.Combine(this.config.StarCraftDir, "Maps", "(2)Bottleneck.scm");

            foreach (string path in new[] { this.config.GuestPath, this.config.DataDir, this.config.BaseMap })
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    this.Monitor.Log($"Missing {path}; check config.json. StardewCraft is disabled.", LogLevel.Error);
                    return;
                }
            }

            this.shm = new Shm();
            this.graphics = new BwGraphics(this.config.DataDir, this.config.Palette, Game1.graphics.GraphicsDevice);
            this.combat = new Combat(this.shm, this.config);
            this.raids = new Raids(this.shm, this.config);
            this.turrets = new Turrets(this.Helper, this.shm);
            this.screenshotter = new Screenshotter(this.config, this.Monitor);
            this.allies = new Allies(this.shm, this.config);
            this.cropRaiders = new CropRaiders(this.shm, this.config);
            this.raiderMemory = new RaiderMemory(this.shm);

            var start = new ProcessStartInfo(this.config.GuestPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(this.config.GuestPath)!,
            };
            start.ArgumentList.Add(this.config.DataDir);
            start.ArgumentList.Add(this.config.BaseMap);
            this.guest = Process.Start(start);
            if (this.guest == null)
            {
                this.Monitor.Log("Could not start the OpenBW guest.", LogLevel.Error);
                return;
            }
            this.guest.OutputDataReceived += (_, a) => { if (a.Data != null) this.Monitor.Log($"[guest] {a.Data}", LogLevel.Debug); };
            this.guest.ErrorDataReceived += (_, a) => { if (a.Data != null) this.Monitor.Log($"[guest] {a.Data}", LogLevel.Warn); };
            this.guest.BeginOutputReadLine();
            this.guest.BeginErrorReadLine();
            this.Monitor.Log($"OpenBW guest started (pid {this.guest.Id}).", LogLevel.Info);
        }

        private void StopGuest()
        {
            try
            {
                if (this.guest is { HasExited: false })
                    this.guest.Kill();
            }
            catch (InvalidOperationException)
            {
                // already gone
            }
        }

        /// <summary>Sends the location's walkability to the guest, which rebuilds its BW map.</summary>
        private void LoadLocation(GameLocation? location)
        {
            if (this.shm == null || location?.Map == null)
                return;

            var layer = location.Map.Layers[0];
            int width = Math.Min(layer.LayerWidth, Shm.GridMax);
            int height = Math.Min(layer.LayerHeight, Shm.GridMax);
            var grid = new byte[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                    grid[y * width + x] = IsWalkable(location, x, y) ? (byte)1 : (byte)0;
            }

            this.mapId++;
            this.sim = new SimLocation(location, this.mapId, width, height, grid);
            this.units = Array.Empty<UnitInfo>();
            this.allies?.Reset();
            this.cropRaiders?.Reset();
            this.shm.WriteGrid(grid);
            this.shm.PushCommand(Shm.CmdLoadMap, width, height, this.mapId);
            this.turrets?.Restore(this.sim);
            this.raiderMemory?.Restore(this.sim);
            this.Monitor.Log($"Simulating {location.NameOrUniqueName} ({width}x{height}) as map {this.mapId}.", LogLevel.Trace);
        }

        private static bool IsWalkable(GameLocation location, int x, int y)
        {
            var tile = new Vector2(x, y);
            if (!location.isTilePassable(new xTile.Dimensions.Location(x, y), Game1.viewport))
                return false;
            if (location.Objects.TryGetValue(tile, out StardewValley.Object? obj) && !obj.isPassable())
                return false;
            if (location.terrainFeatures.TryGetValue(tile, out var feature) && !feature.isPassable())
                return false;
            foreach (var clump in location.resourceClumps)
            {
                if (clump.occupiesTile(x, y))
                    return false;
            }
            foreach (var building in location.buildings)
            {
                if (building.occupiesTile(tile) && !building.isTilePassable(tile))
                    return false;
            }
            return true;
        }

        private bool FarmerInSim => this.sim != null && this.sim.Location == Game1.currentLocation;

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (this.shm == null)
                return;
            this.shm.Heartbeat();

            uint status = this.shm.GuestStatus;
            if (status != this.lastGuestStatus)
            {
                this.lastGuestStatus = status;
                if (status == 3)
                    this.Monitor.Log("The OpenBW guest reported an error; see [guest] lines above.", LogLevel.Error);
            }

            if (!Context.IsWorldReady || this.sim == null)
                return;

            bool present = this.FarmerInSim;

            UnitInfo[]? fresh = this.shm.ReadUnits(this.sim.MapId);
            if (fresh != null)
            {
                this.units = fresh;
                this.turrets!.Observe(this.sim, fresh);
                this.raiderMemory!.Observe(this.sim, fresh);
            }
            this.turrets!.Update();

            if (present)
            {
                this.allies!.Update(this.sim.Location, this.units);
                this.cropRaiders!.Update(this.sim.Location, this.units);
            }
            if (present && Context.IsPlayerFree)
                this.combat!.Update(this.sim.Location, this.units);

            uint damageTotal = this.shm.FarmerDamageTotal;
            if (damageTotal > this.lastDamageTotal && present && Game1.player.CanMove && !Game1.player.temporarilyInvincible)
                Game1.player.takeDamage((int)(damageTotal - this.lastDamageTotal), false, null);
            this.lastDamageTotal = damageTotal;
        }

        private void OnTimeChanged(object? sender, TimeChangedEventArgs e)
        {
            if (!this.config.NightRaids || !this.FarmerInSim || this.sim!.Location is not Farm)
                return;
            if (e.NewTime < this.config.NightRaidStartTime || e.NewTime % 100 != 0)
                return;

            int stage = this.raids!.NightStage();
            if (this.raids.Launch(this.sim, this.units.Count(u => u.IsRaider), stage, fromEdge: true) == null)
            {
                Game1.addHUDMessage(new HUDMessage($"Night raid! Stage {stage + 1}: {BwUnits.Stages[stage].Name}", HUDMessage.error_type));
            }
        }

        private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
        {
            if (e.Button == this.config.ScreenshotKey)
            {
                this.TakeScreenshot();
                return;
            }
            if (this.shm == null || !Context.IsPlayerFree || !this.FarmerInSim)
                return;

            if (e.Button == this.config.RaidKey)
            {
                this.ShowStageMenu(page: 0);
            }
            else if (e.Button == this.config.ClearKey)
            {
                this.shm.PushCommand(Shm.CmdClear, 0, 0, 0);
            }
            else if (e.Button == this.config.WeaponKitKey)
            {
                this.GiveWeaponKit();
            }
            else if (e.Button == this.config.BuildTankKey || e.Button == this.config.BuildAntiAirKey)
            {
                var kind = e.Button == this.config.BuildTankKey ? BwUnits.SiegeTank : BwUnits.AntiAir;
                Vector2 tile = e.Cursor.Tile;
                string? error = this.turrets!.TryBuild(this.sim!, kind, (int)tile.X, (int)tile.Y, free: !this.config.TurretsCostCrops);
                if (error != null)
                    Game1.addHUDMessage(new HUDMessage(error, HUDMessage.error_type));
                this.Helper.Input.Suppress(e.Button);
            }
        }

        private const int StagesPerPage = 5;

        /// <summary>Asks which stage to summon, five per page so the dialogue fits on screen.</summary>
        private void ShowStageMenu(int page)
        {
            int first = page * StagesPerPage;
            int last = Math.Min(first + StagesPerPage, BwUnits.Stages.Count);
            var responses = new System.Collections.Generic.List<Response>();
            for (int i = first; i < last; i++)
            {
                var stage = BwUnits.Stages[i];
                int count = stage.Groups.Sum(g => g.Count);
                responses.Add(new Response($"stage{i}", $"{i + 1}. {stage.Name} ({count} units)"));
            }
            if (last < BwUnits.Stages.Count)
                responses.Add(new Response("next", "More stages..."));
            if (page > 0)
                responses.Add(new Response("prev", "Back"));
            responses.Add(new Response("cancel", "Cancel"));

            Game1.currentLocation.createQuestionDialogue("Summon which raid?", responses.ToArray(), (_, answer) =>
            {
                if (answer == "next" || answer == "prev")
                {
                    int nextPage = answer == "next" ? page + 1 : page - 1;
                    DelayedAction.functionAfterDelay(() => this.ShowStageMenu(nextPage), 50);
                }
                else if (answer.StartsWith("stage") && int.TryParse(answer.AsSpan(5), out int stage) && this.FarmerInSim)
                {
                    string? error = this.raids!.Launch(this.sim!, this.units.Count(u => u.IsRaider), stage, fromEdge: false);
                    Game1.addHUDMessage(new HUDMessage(error ?? $"Stage {stage + 1}: {BwUnits.Stages[stage].Name}!", HUDMessage.error_type));
                }
            });
        }

        private void TakeScreenshot()
        {
            string? file = this.screenshotter?.Take();
            if (file == null)
            {
                Game1.playSound("cancel");
                return;
            }
            // Sound only: a HUD message would end up in the next screenshot.
            Game1.playSound("cameraNoise");
        }

        private void GiveWeaponKit()
        {
            foreach (string id in WeaponKit)
            {
                if (ItemRegistry.Exists(id))
                    Game1.player.addItemByMenuIfNecessary(ItemRegistry.Create(id));
            }
            Game1.player.addItemByMenuIfNecessary(ItemRegistry.Create("(O)380", 200)); // iron ore slingshot ammo
            Game1.addHUDMessage(new HUDMessage("Weapons delivered. Go get 'em.", HUDMessage.achievement_type));
        }

        private void OnRenderedWorld(object? sender, RenderedWorldEventArgs e)
        {
            if (this.shm == null || this.graphics == null || !this.FarmerInSim)
                return;

            Snapshot? snap = this.shm.ReadSnapshot();
            if (snap == null || snap.Value.MapId != (uint)this.sim!.MapId)
                return;

            for (int i = 0; i < snap.Value.Count; i++)
            {
                ImageEntry img = snap.Value[i];
                Texture2D? tex = this.graphics.GetFrame(img);
                if (tex == null)
                    continue;

                // BW px -> Stardew world px is x2; sprites are drawn at 2x to match.
                Vector2 screen = Game1.GlobalToLocal(Game1.viewport, new Vector2(img.X * 2, img.Y * 2));
                e.SpriteBatch.Draw(tex, screen, null, Color.White, 0f, Vector2.Zero, 2f,
                    img.Flipped ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 1f);
            }

            this.allies?.Draw(e.SpriteBatch);
            if (this.config.ShowHealthBars)
                this.DrawHealthBars(e.SpriteBatch);
        }

        private void DrawHealthBars(SpriteBatch b)
        {
            const int width = 40;
            foreach (UnitInfo unit in this.units)
            {
                if (unit.MaxHp <= 0 || (unit.Hp >= unit.MaxHp && unit.Shields >= unit.MaxShields))
                    continue;

                Vector2 top = Game1.GlobalToLocal(Game1.viewport, unit.WorldPosition + new Vector2(-width / 2, unit.Building ? -72 : -44));
                int x = (int)top.X, y = (int)top.Y;
                Color fill = unit.Owner == BwUnits.FarmerOwner ? Color.LimeGreen : Color.Red;
                b.Draw(Game1.staminaRect, new Rectangle(x - 1, y - 1, width + 2, 6), Color.Black * 0.7f);
                b.Draw(Game1.staminaRect, new Rectangle(x, y, width * Math.Max(0, unit.Hp) / unit.MaxHp, 4), fill);
                if (unit.MaxShields > 0)
                {
                    b.Draw(Game1.staminaRect, new Rectangle(x - 1, y - 5, width + 2, 4), Color.Black * 0.7f);
                    b.Draw(Game1.staminaRect, new Rectangle(x, y - 4, width * Math.Max(0, unit.Shields) / unit.MaxShields, 2), Color.DeepSkyBlue);
                }
            }
        }
    }
}
