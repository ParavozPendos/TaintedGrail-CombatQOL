using System;
using Awaken.TG.Main.AI.Fights.Projectiles;
using Awaken.TG.Main.Fights.DamageInfo;
using Awaken.TG.Main.Fights.Factions;
using Awaken.TG.Main.Fights.NPCs;
using Awaken.TG.Main.Heroes;
using UnityEngine;

namespace CombatQOL;

// Wider hit zone for the hero's arrows, thrown weapons and spell projectiles.
//
// Every physics step DamageDealingProjectile.ThrowCast casts a thin sphere (RaycastRadius) from
// rb.position along rb.velocity for speed*dt; whatever that thin cast touches is what gets hit,
// and the game decides weak-spot (headshot) damage from the collider it touched.
// We never replace that hit test. Before it runs we look ahead with a WIDE sphere; only when the
// real thin path misses everything this step but the wide one finds a hostile, we nudge the
// projectile's velocity toward that enemy, so the game's own thin cast lands on the next step.
//
// Weak-spot check ("narrow crit arrow"): before nudging we cast a narrow probe along the TRUE
// trajectory a little further ahead (look-ahead). If the probe would hit that enemy by itself,
// we do nothing — the real arrow hits exactly where it flies, weak spot or not. Otherwise this is
// a near miss and we nudge toward the torso (an ordinary hit), so a wide zone never "steals" or
// gifts a headshot.
internal static class Projectiles
{
    internal static float LastVanilla = -1f, LastWide;
    internal static int Nudges;
    private static int _errors, _logged, _shots;

    private static bool SafeBox(DamageDealingProjectile p) { try { return p.UseBoxCast; } catch { return false; } }
    internal static bool Disabled;
    private static ObjId _lastShot;

    private enum Kind { Arrow, Thrown, Spell }

    internal static void Before(DamageDealingProjectile p, float dt)
    {
        if (Disabled || !Plugin.On || p == null || dt <= 0f) return;
        if (p._destroyed || p._contactMade || p._deflected) return;
        var hero = Hero.Current;
        var owner = p.owner;
        if (hero == null || owner == null || owner.Id() != hero.Id()) return;

        Kind kind = p.TryCast<MagicProjectile>() != null ? Kind.Spell : p.TryCast<Arrow>() != null ? Kind.Arrow : Kind.Thrown;
        bool enabled; float add; bool crit;
        if (kind == Kind.Spell) { enabled = Plugin.WideSpells.Value; add = Plugin.SpellExtraRadius.Value; crit = false; }
        else
        {
            enabled = Plugin.WideArrows.Value && (kind == Kind.Arrow || Plugin.WideThrown.Value);
            add = Plugin.ArrowExtraRadius.Value; crit = Plugin.CritCheck.Value;
        }
        if (!enabled) return;

        var rb = p._rb;
        if (rb == null) return;
        Vector3 v = rb.linearVelocity;
        float speed = v.magnitude;
        if (speed < 2f) return;
        Vector3 dir = v / speed, origin = rb.position;
        float dist = speed * dt;

        float rVanilla = p.RaycastRadius;
        try { if (p.UseBoxCast) { var b = p.BoxCastSize; rVanilla = Mathf.Max(rVanilla, Mathf.Max(b.x, b.y) * 0.5f); } } catch { }
        rVanilla = Mathf.Max(0.005f, rVanilla);
        float rWide = rVanilla + Mathf.Max(0f, add);
        if (p.Id() != _lastShot)
        {
            _lastShot = p.Id(); Nudges = 0;
            if (Plugin.LogHits.Value && _shots++ < 10) Plugin.L.LogInfo($"[projectiles] hero {kind}: game radius {rVanilla:0.000} m → {rWide:0.00} m, box cast {SafeBox(p)}");
        }
        LastVanilla = rVanilla; LastWide = rWide;
        if (rWide <= rVanilla + 0.002f) return;

        int mask = p.HitMask.value;
        if (Plugin.ShowHitboxes.Value) Overlay.AddRing(origin, dir, rWide, kind == Kind.Spell);

        // 1) the real thin path touches something this step — the game handles it, we stay out
        if (Physics.SphereCast(origin, rVanilla, dir, out _, dist, mask, QueryTriggerInteraction.Collide)) return;

        // Physics.SphereCastAll is stripped from this game build — only single casts are available.
        // Casting against the hitbox layer only makes the first hit a character, not the ground.
        int charMask = CharacterMask(mask);

        // 2) wide look for a hostile enemy within this step (skip friendly/dead/already hit, up to 4 times)
        NpcElement target = null; Collider targetCol = null; Vector3 sweepCenter = default;
        float travelled = 0f;
        for (int attempt = 0; attempt < 4 && travelled < dist; attempt++)
        {
            var from = origin + dir * travelled;
            if (!Physics.SphereCast(from, rWide, dir, out var h, dist - travelled, charMask, QueryTriggerInteraction.Collide)) break;
            var col = h.collider;
            if (col == null) break;
            var npc = Enemy(col, hero, p);
            if (npc != null) { target = npc; targetCol = col; sweepCenter = from + dir * h.distance; break; }
            travelled += h.distance + 0.05f;               // not a valid target — look past it
        }
        if (target == null) return;

        // 3) weak-spot check: would a narrow arrow on the true path hit this enemy soon anyway?
        if (crit)
        {
            float probeR = Mathf.Max(0.002f, rVanilla * Mathf.Clamp(Plugin.CritProbePercent.Value, 10f, 100f) / 100f);
            float look = dist + speed * Mathf.Clamp(Plugin.CritLookahead.Value, 0f, 0.5f);
            // Any character hitbox on the true path (incl. separate "Weakspot" colliders that do not map to the
            // enemy model) means the real arrow will hit by itself — never redirect it, the game decides the spot.
            if (Physics.SphereCast(origin, probeR, dir, out var ph, look, charMask, QueryTriggerInteraction.Collide)
                && ph.collider != null) return;
        }

        // 4) nudge: crit mode → torso (ordinary hit); simple mode → nearest point of the touched collider
        Vector3 aim = crit || kind == Kind.Spell ? Assist.AimPoint(target) : targetCol.ClosestPoint(sweepCenter);
        Vector3 to = aim - origin;
        float len = to.magnitude;
        if (len < 0.05f) return;
        Vector3 nd = to / len;
        if (Vector3.Angle(dir, nd) > 40f) return;           // never bend a shot sharply
        // walls between the projectile and the aim point stop the nudge
        if (Physics.Raycast(origin, nd, out var wall, len, mask, QueryTriggerInteraction.Ignore) && !SameAlive(wall.collider, target)) return;

        rb.linearVelocity = nd * speed;
        try { var t = p._transform; if (t != null) t.rotation = Quaternion.LookRotation(nd); } catch { }
        Nudges++;
        if (Plugin.LogHits.Value && _logged++ < 40)
            Plugin.L.LogInfo($"[{kind}] nudged {Vector3.Angle(dir, nd):0.0}° toward {SafeName(target)} (r {rVanilla:0.000} → {rWide:0.00} m, {(crit ? "probe missed — body hit" : "nearest point")})");
    }

    private static int _hitboxLayer = -2;

    private static int CharacterMask(int mask)
    {
        if (_hitboxLayer == -2)
        {
            try { _hitboxLayer = LayerMask.NameToLayer("Hitboxes"); } catch { _hitboxLayer = -1; }
            Plugin.L.LogInfo($"[projectiles] hitbox layer = {_hitboxLayer}");
        }
        if (_hitboxLayer < 0) return mask;
        int m = mask & (1 << _hitboxLayer);
        return m != 0 ? m : (1 << _hitboxLayer);
    }

    private static string SafeName(NpcElement n) { try { return n.Name; } catch { return "?"; } }

    // Collider → living hostile NPC that this projectile has not hit yet (else null).
    private static NpcElement Enemy(Collider col, Hero hero, DamageDealingProjectile p)
    {
        try
        {
            Damage.DetermineTargetHit(col, out var receiver, out _);
            if (receiver == null || receiver.Id() == hero.Id()) return null;
            var npc = Compat.As<NpcElement>(receiver);
            if (npc == null || !npc.IsAlive || npc.IsDying) return null;
            if (!WithFactionUtils.IsHostileTo(hero, npc)) return null;
            var hitList = p._alivesHit;
            if (hitList != null)
                for (int i = 0; i < hitList.Count; i++)
                    if (hitList[i] != null && hitList[i].Id() == receiver.Id()) return null;
            return npc;
        }
        catch { return null; }
    }

    private static bool SameAlive(Collider col, NpcElement target)
    {
        if (col == null) return false;
        try
        {
            Damage.DetermineTargetHit(col, out var receiver, out _);
            return receiver != null && receiver.Id() == target.Id();
        }
        catch { return false; }
    }

    internal static void Fail(Exception e)
    {
        _errors++;
        if (_errors <= 5) Plugin.L.LogError("Projectiles: " + e);
        if (_errors == 20) { Disabled = true; Plugin.L.LogError("Projectiles: too many errors, wide projectiles are off until restart"); }
    }
}
