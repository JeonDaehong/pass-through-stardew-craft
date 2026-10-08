using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.ItemTypeDefinitions;

namespace StardewCraft
{
    /// <summary>
    /// Everything on the Stardew side that raiders can attack besides the farmer: farm animals
    /// (which flee or headbutt back) and villagers (who draw swords). None of them can die.
    /// </summary>
    internal sealed class Allies
    {
        private const int SwingTicks = 14;
        private const float VillagerReach = 96f; // BW px, 3 tiles
        private const float AnimalReach = 72f;

        private static readonly string[] BattleCries =
        {
            "Get off my lawn!", "Take that!", "Not in Pelican Town!", "Hyah!", "Begone, bug!", "For the valley!",
        };

        private sealed class AllyState
        {
            public uint Id;
            public uint DamageSeen;
            public int Cooldown;
            public int FleeTicks;
            public int FleeDirection;
            public float NormalSpeed;
        }

        private sealed class Swing
        {
            public NPC Npc = null!;
            public float Angle;
            public int Tick;
        }

        private readonly Shm shm;
        private readonly ModConfig config;
        private readonly Dictionary<string, AllyState> states = new();
        private readonly List<Swing> swings = new();
        private readonly List<Proxy> proxies = new();
        private readonly Dictionary<long, int> animalHp = new(); // by FarmAnimal.myID; cleared each morning
        private uint nextId = 1;
        private ParsedItemData? sword;

        public Allies(Shm shm, ModConfig config)
        {
            this.shm = shm;
            this.config = config;
        }

        /// <summary>Animals sleep their wounds off overnight.</summary>
        public void ResetDay()
        {
            this.animalHp.Clear();
        }

        /// <summary>Forgets proxy ids when the simulated location changes.</summary>
        public void Reset()
        {
            this.states.Clear();
            this.swings.Clear();
            this.nextId = 1;
        }

        public void Update(GameLocation location, IReadOnlyList<UnitInfo> units)
        {
            this.proxies.Clear();
            Vector2 feet = Game1.player.getStandingPosition();
            this.proxies.Add(new Proxy(0, (int)feet.X / 2, (int)feet.Y / 2, ProxyKind.Farmer));

            var damage = this.shm.ReadProxyDamage();
            var raiders = units.Where(u => u.IsRaider).ToList();

            if (this.config.AnimalsJoinFights)
            {
                var dead = new List<FarmAnimal>();
                foreach (FarmAnimal animal in location.animals.Values)
                {
                    var state = this.Track("a" + animal.myID.Value, animal, IsBig(animal) ? ProxyKind.LargeAnimal : ProxyKind.SmallAnimal);
                    if (state == null)
                        continue;
                    int hurt = this.TookDamage(state, damage);
                    if (hurt > 0 && this.Wound(animal, hurt))
                    {
                        dead.Add(animal);
                        continue;
                    }
                    if (hurt > 0)
                        this.AnimalHurt(location, animal, state, raiders);
                    this.UpdateFlee(animal, state);
                }
                foreach (FarmAnimal animal in dead)
                    this.KillAnimal(location, animal);
            }

            if (this.config.VillagersJoinFights)
            {
                foreach (NPC npc in location.characters)
                {
                    if (!npc.IsVillager)
                        continue;
                    var state = this.Track("n" + npc.Name, npc, ProxyKind.Villager);
                    if (state == null)
                        continue;
                    bool hurt = this.TookDamage(state, damage) > 0;
                    this.VillagerFight(location, npc, state, raiders, hurt);
                }
            }

            this.shm.WriteProxies(this.proxies);
            for (int i = this.swings.Count - 1; i >= 0; i--)
            {
                if (++this.swings[i].Tick >= SwingTicks)
                    this.swings.RemoveAt(i);
            }
        }

        private AllyState? Track(string key, Character who, ProxyKind kind)
        {
            if (this.proxies.Count >= Shm.ProxyCapacity)
                return null;
            if (!this.states.TryGetValue(key, out var state))
                this.states[key] = state = new AllyState { Id = this.nextId++, NormalSpeed = who.speed };
            Vector2 pos = who.getStandingPosition();
            this.proxies.Add(new Proxy(state.Id, (int)pos.X / 2, (int)pos.Y / 2, kind));
            if (state.Cooldown > 0)
                state.Cooldown--;
            return state;
        }

        /// <summary>Returns the damage this proxy absorbed since the last check.</summary>
        private int TookDamage(AllyState state, Dictionary<uint, uint> damage)
        {
            if (!damage.TryGetValue(state.Id, out uint total) || total <= state.DamageSeen)
                return 0;
            int delta = (int)(total - state.DamageSeen);
            state.DamageSeen = total;
            return delta;
        }

        // ---- animals --------------------------------------------------------------

        private int MaxHp(FarmAnimal animal) => IsBig(animal) ? this.config.LargeAnimalHp : this.config.SmallAnimalHp;

        /// <summary>Applies damage to the animal's health pool; returns true if it died.</summary>
        private bool Wound(FarmAnimal animal, int damage)
        {
            int max = this.MaxHp(animal);
            int hp = (this.animalHp.TryGetValue(animal.myID.Value, out int current) ? current : max) - damage;
            this.animalHp[animal.myID.Value] = hp;
            return this.config.AnimalsCanDie && hp <= 0;
        }

        private void KillAnimal(GameLocation location, FarmAnimal animal)
        {
            long id = animal.myID.Value;
            location.temporarySprites.Add(new TemporaryAnimatedSprite(5, animal.Position, Color.White, 8, false, 60f));
            location.playSound("hitEnemy");
            if (animal.home?.GetIndoors() is AnimalHouse house)
                house.animalsThatLiveHere.Remove(id);
            location.animals.Remove(id);
            this.animalHp.Remove(id);
            this.states.Remove("a" + id);
            Game1.addHUDMessage(new HUDMessage($"{animal.displayName} was killed by raiders!", HUDMessage.error_type));
        }

        private void AnimalHurt(GameLocation location, FarmAnimal animal, AllyState state, List<UnitInfo> raiders)
        {
            if (state.Cooldown > 0)
                return;
            state.Cooldown = 60;

            UnitInfo? attacker = Nearest(raiders, animal.getStandingPosition(), AnimalReach * 2);
            bool big = IsBig(animal);
            if (attacker != null && Game1.random.NextDouble() < this.config.AnimalCounterChance)
            {
                // Short counter-attack: a headbutt (or peck) at whoever is closest.
                int dmg = big ? Game1.random.Next(10, 19) : Game1.random.Next(4, 9);
                animal.faceGeneralDirection(attacker.Value.WorldPosition, 0, false, false);
                animal.doEmote(Character.angryEmote);
                this.Hit(location, attacker.Value, dmg, new Color(120, 255, 120));
                return;
            }

            animal.doEmote(Character.exclamationEmote);
            try { animal.makeSound(); } catch { /* some animals have no sound */ }

            // Run directly away from the attacker.
            Vector2 away = attacker != null ? animal.getStandingPosition() - attacker.Value.WorldPosition : new Vector2(Game1.random.Next(-1, 2), 1);
            state.FleeDirection = Math.Abs(away.X) > Math.Abs(away.Y) ? (away.X > 0 ? 1 : 3) : (away.Y > 0 ? 2 : 0);
            state.FleeTicks = 90;
        }

        private void UpdateFlee(FarmAnimal animal, AllyState state)
        {
            if (state.FleeTicks <= 0)
                return;
            if (--state.FleeTicks == 0)
            {
                animal.speed = (int)state.NormalSpeed;
                animal.Halt();
                return;
            }
            animal.speed = (int)Math.Max(state.NormalSpeed * 2, 4);
            if (animal.FacingDirection != state.FleeDirection || !animal.isMoving())
            {
                animal.Halt();
                animal.faceDirection(state.FleeDirection);
                animal.setMovingInFacingDirection();
            }
        }

        // ---- villagers ------------------------------------------------------------

        private void VillagerFight(GameLocation location, NPC npc, AllyState state, List<UnitInfo> raiders, bool hurt)
        {
            if (hurt && Game1.random.NextDouble() < 0.15)
                npc.showTextAboveHead(Game1.random.NextDouble() < 0.5 ? "Ow!" : "Hey!", duration: 1500);

            if (state.Cooldown > 0)
                return;
            UnitInfo? target = Nearest(raiders, npc.getStandingPosition(), VillagerReach * 2);
            if (target == null)
                return;

            state.Cooldown = 50;
            npc.faceGeneralDirection(target.Value.WorldPosition, 0, false, false);
            Vector2 d = target.Value.WorldPosition - npc.getStandingPosition();
            this.swings.Add(new Swing { Npc = npc, Angle = (float)Math.Atan2(d.Y, d.X) });
            location.playSound("swordswipe");

            int dmg = Game1.random.Next(this.config.VillagerMinDamage, this.config.VillagerMaxDamage + 1);
            this.Hit(location, target.Value, dmg, new Color(255, 230, 120));
            if (Game1.random.NextDouble() < 0.2)
                npc.showTextAboveHead(BattleCries[Game1.random.Next(BattleCries.Length)], duration: 2000);
        }

        /// <summary>Draws the villagers' sword swings: a blade sweeping across the target direction.</summary>
        public void Draw(SpriteBatch b)
        {
            foreach (var (animal, hp, max) in this.WoundedAnimals())
            {
                const int width = 40;
                Vector2 top = Game1.GlobalToLocal(Game1.viewport, animal.getStandingPosition() + new Vector2(-width / 2, IsBig(animal) ? -110 : -70));
                b.Draw(Game1.staminaRect, new Rectangle((int)top.X - 1, (int)top.Y - 1, width + 2, 6), Color.Black * 0.7f);
                b.Draw(Game1.staminaRect, new Rectangle((int)top.X, (int)top.Y, width * Math.Max(0, hp) / max, 4), Color.Orange);
            }

            if (this.swings.Count == 0)
                return;
            this.sword ??= ItemRegistry.GetDataOrErrorItem("(W)0");
            Texture2D tex = this.sword.GetTexture();
            Rectangle src = this.sword.GetSourceRect();

            foreach (var swing in this.swings)
            {
                float t = swing.Tick / (float)SwingTicks;
                // Weapon sprites point up-right (-45 degrees); sweep 120 degrees across the target.
                float rotation = swing.Angle + MathHelper.PiOver4 + MathHelper.Lerp(-MathHelper.Pi / 3, MathHelper.Pi / 3, t);
                Vector2 hand = Game1.GlobalToLocal(Game1.viewport, swing.Npc.getStandingPosition() + new Vector2(0, -40));
                b.Draw(tex, hand, src, Color.White * (1f - t * 0.3f), rotation, new Vector2(0, src.Height), 4f, SpriteEffects.None, 1f);
            }
        }

        // ---- shared ---------------------------------------------------------------

        private IEnumerable<(FarmAnimal Animal, int Hp, int Max)> WoundedAnimals()
        {
            if (!this.config.AnimalsCanDie || this.animalHp.Count == 0)
                yield break;
            foreach (FarmAnimal animal in Game1.currentLocation.animals.Values)
            {
                if (this.animalHp.TryGetValue(animal.myID.Value, out int hp))
                    yield return (animal, hp, this.MaxHp(animal));
            }
        }

        /// <summary>Barn animals (cows, goats, pigs...) hit harder than coop animals.</summary>
        private static bool IsBig(FarmAnimal animal)
        {
            return animal.GetAnimalData()?.House != "Coop";
        }

        private void Hit(GameLocation location, UnitInfo unit, int damage, Color color)
        {
            this.shm.Damage(unit.Id, damage);
            location.debris.Add(new Debris(damage, unit.WorldPosition - new Vector2(0, 48), color, 1f, null));
        }

        private static UnitInfo? Nearest(List<UnitInfo> raiders, Vector2 worldPos, float maxWorldDistance)
        {
            UnitInfo? best = null;
            float bestDist = maxWorldDistance;
            foreach (var u in raiders)
            {
                float d = Vector2.Distance(u.WorldPosition, worldPos);
                if (d <= bestDist)
                {
                    best = u;
                    bestDist = d;
                }
            }
            return best;
        }
    }
}
