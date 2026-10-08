using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Projectiles;
using StardewValley.Tools;

namespace StardewCraft
{
    /// <summary>Turns the farmer's sword swings and slingshot shots into damage on OpenBW units.</summary>
    internal sealed class Combat
    {
        private const float UnitRadius = 28f; // world px; roughly a small BW unit drawn at 2x

        private readonly Shm shm;
        private readonly ModConfig config;
        private bool wasUsingTool;

        public Combat(Shm shm, ModConfig config)
        {
            this.shm = shm;
            this.config = config;
        }

        public void Update(GameLocation location, IReadOnlyList<UnitInfo> units)
        {
            Farmer player = Game1.player;
            bool usingTool = player.UsingTool;
            if (usingTool && !this.wasUsingTool && player.CurrentTool is MeleeWeapon weapon && !weapon.isScythe())
                this.Swing(location, weapon, units);
            this.wasUsingTool = usingTool;

            this.CheckProjectiles(location, units);
        }

        private void Swing(GameLocation location, MeleeWeapon weapon, IReadOnlyList<UnitInfo> units)
        {
            Farmer player = Game1.player;
            Vector2 facing = player.FacingDirection switch
            {
                0 => new Vector2(0, -1),
                1 => new Vector2(1, 0),
                2 => new Vector2(0, 1),
                _ => new Vector2(-1, 0),
            };
            float radius = weapon.type.Value switch
            {
                MeleeWeapon.dagger => 56f,
                MeleeWeapon.club => 104f,
                _ => 84f,
            };
            Vector2 center = player.getStandingPosition() + facing * (radius * 0.7f);

            bool hitAny = false;
            foreach (UnitInfo unit in units)
            {
                if (!unit.IsRaider || Vector2.Distance(unit.WorldPosition, center) > radius + UnitRadius)
                    continue;

                int damage = Game1.random.Next(weapon.minDamage.Value, weapon.maxDamage.Value + 1);
                bool crit = Game1.random.NextDouble() < weapon.critChance.Value;
                if (crit)
                    damage = (int)(damage * weapon.critMultiplier.Value);
                damage = Math.Max(1, (int)(damage * this.config.WeaponDamageMultiplier));

                this.shm.Damage(unit.Id, damage);
                ShowDamage(location, unit, damage, crit);
                hitAny = true;
            }
            if (hitAny)
                location.playSound(weapon.type.Value == MeleeWeapon.club ? "clubhit" : "hitEnemy");
        }

        private void CheckProjectiles(GameLocation location, IReadOnlyList<UnitInfo> units)
        {
            if (location.projectiles.Count == 0)
                return;

            foreach (Projectile projectile in location.projectiles.ToList())
            {
                if (projectile.theOneWhoFiredMe.Get(location) != Game1.player)
                    continue;

                Rectangle box = projectile.getBoundingBox();
                box.Inflate(UnitRadius, UnitRadius);
                foreach (UnitInfo unit in units)
                {
                    Vector2 pos = unit.WorldPosition;
                    if (!unit.IsRaider || !box.Contains((int)pos.X, (int)pos.Y))
                        continue;

                    int baseDamage = projectile is BasicProjectile basic ? basic.damageToFarmer.Value : 10;
                    int damage = Math.Max(1, (int)(baseDamage * this.config.SlingshotDamageMultiplier));
                    this.shm.Damage(unit.Id, damage);
                    ShowDamage(location, unit, damage, crit: false);
                    location.playSound("hitEnemy");
                    location.projectiles.Remove(projectile);
                    break;
                }
            }
        }

        private static void ShowDamage(GameLocation location, UnitInfo unit, int damage, bool crit)
        {
            Vector2 pos = unit.WorldPosition - new Vector2(0, 48);
            location.debris.Add(new Debris(damage, pos, crit ? Color.Yellow : new Color(255, 130, 0), crit ? 1.5f : 1f, null));
        }
    }
}
