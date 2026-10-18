using System;
using System.Collections.Generic;
using Awaken.TG.Main.AI.Fights.Projectiles;
using Awaken.TG.Main.Fights.DamageInfo;
using Awaken.TG.Main.Fights.NPCs;
using Awaken.TG.Main.Heroes;
using UnityEngine;
using TGShared;

namespace CombatQOL;

// Hit feedback for the hero's damage (postfix of HealthElement.TakeDamageInternal, after the game resolved it):
//  • thin hitmarker at the crosshair for bows and spell projectiles: ivory = hit, amber = weak spot,
//    crimson = crit, crimson with an amber rim = crit on a weak spot;
//  • floating "Crit!" / "Weak spot!" labels above the enemy (melee, bows, magic).
// Crit: rolled in CheckCriticalProbability (remembered per damage object) or forced by Damage.Critical.
// Weak spot: the game's own rule — Damage.WeakSpotMultiplier > 1 (or WeakSpotHit).
internal static class HitFeedback
{
    private enum Kind { Melee, Bow, Magic, Other }

    private struct Marker { public float T; public bool Crit, Weak; }
    private struct Label { public string Text; public Vector3 World; public bool HasWorld; public float T; public Color C; public float Jitter; }

    private static readonly List<Marker> Markers = new();
    private static readonly List<Label> Labels = new();
    private static readonly Dictionary<ObjId, float> CritRolls = new();
    private static float _lastMarker;
    private static int _errors;

    // palette close to the game's UI: parchment ivory, amber gold, muted crimson
    private static readonly Color Ivory = new(0.94f, 0.91f, 0.84f, 1f);
    private static readonly Color Amber = new(0.92f, 0.71f, 0.30f, 1f);
    private static readonly Color Crimson = new(0.86f, 0.24f, 0.19f, 1f);

    internal static void MarkCrit(ObjId damage)
    {
        if (CritRolls.Count > 64) CritRolls.Clear();
        CritRolls[damage] = Time.unscaledTime;
    }

    internal static void OnDamage(Damage damage)
    {
        if (!Plugin.On || damage == null || _errors > 20) return;
        bool rolled = CritRolls.Remove(damage.Id());
        try
        {
            var hero = Hero.Current;
            if (hero == null) return;
            var dealer = damage.DamageDealer;
            if (dealer == null || dealer.Id() != hero.Id()) return;
            var target = damage.Target;
            if (target == null || target.Id() == hero.Id()) return;

            bool crit = rolled || damage.Critical;
            bool weak = damage.WeakSpotHit || damage.WeakSpotMultiplier > 1.001f;
            Kind kind = Classify(damage, out bool projectile);
            if (kind == Kind.Magic) weak = false;

            if (projectile && (kind == Kind.Bow || kind == Kind.Magic)) AddMarker(kind, crit, weak);

            if (Plugin.FloatText.Value && (crit || weak))
            {
                bool cat = kind switch { Kind.Bow => Plugin.FtBows.Value, Kind.Magic => Plugin.FtMagic.Value, _ => Plugin.FtMelee.Value };
                bool showCrit = crit && Plugin.FtCrits.Value, showWeak = weak && Plugin.FtWeak.Value;
                if (cat && (showCrit || showWeak))
                {
                    string text = showCrit && showWeak ? Loc.T("ft_both") : showCrit ? Loc.T("ft_crit") : Loc.T("ft_weak");
                    var lbl = new Label { Text = text, T = Time.unscaledTime, C = showCrit ? Crimson : Amber, Jitter = UnityEngine.Random.Range(-24f, 24f) };
                    var npc = Compat.As<NpcElement>(target);
                    if (npc != null) { lbl.World = Assist.AimPoint(npc) + Vector3.up * 0.55f; lbl.HasWorld = true; }
                    if (Labels.Count > 8) Labels.RemoveAt(0);
                    Labels.Add(lbl);
                }
            }
            if (Plugin.LogHits.Value && (crit || weak)) Plugin.L.LogInfo($"[hitfx] {kind} crit={crit} weak={weak} (x{damage.WeakSpotMultiplier:0.00})");
        }
        catch (Exception e) { if (++_errors <= 3) Plugin.L.LogWarning("HitFeedback: " + e.Message); }
    }

    private static void AddMarker(Kind kind, bool crit, bool weak)
    {
        bool show;
        if (kind == Kind.Bow)
        {
            if (!Plugin.BowHitmarker.Value) return;
            if (crit && !Plugin.BowHmCrit.Value) crit = false;          // crits hidden → show what remains
            if (weak && !Plugin.BowHmWeak.Value) weak = false;
            show = crit || weak || Plugin.BowHmNormal.Value;
        }
        else
        {
            if (!Plugin.MagicHitmarker.Value) return;
            if (crit && !Plugin.MagicHmCrit.Value) crit = false;
            show = crit || Plugin.MagicHmNormal.Value;
        }
        if (!show || Time.unscaledTime - _lastMarker < 0.04f) return;
        _lastMarker = Time.unscaledTime;
        if (Markers.Count > 6) Markers.RemoveAt(0);
        Markers.Add(new Marker { T = Time.unscaledTime, Crit = crit, Weak = weak });
    }

    private static Kind Classify(Damage damage, out bool projectile)
    {
        projectile = false;
        try
        {
            var p = damage.Projectile;
            if (p != null)
            {
                projectile = true;
                return p.TryCast<MagicProjectile>() != null ? Kind.Magic : Kind.Bow;
            }
        }
        catch { }
        try
        {
            var tpl = damage.Item?.Template;
            if (tpl != null)
            {
                if (tpl.IsCastMagic || tpl.IsRod) return Kind.Magic;
                if (tpl.IsRanged) return Kind.Bow;
                return Kind.Melee;
            }
        }
        catch { }
        return Kind.Melee;   // fists and anything unidentified count as melee
    }

    // ---------- drawing (OnGUI, inside Ui.Begin) ----------
    private const float MarkerLife = 0.30f, LabelLife = 1.25f;
    private static Texture2D _mark, _rim;

    // Anti-aliased "x" with a gap in the middle, generated once (white; tinted when drawn).
    private static Texture2D MarkTex(float halfWidth)
    {
        const int N = 64;
        var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        float c = (N - 1) / 2f, r1 = N * 0.18f, r2 = N * 0.47f;
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float px = x - c, py = y - c;
                float best = 999f;
                for (int i = 0; i < 4; i++)
                {
                    float dx = (i == 0 || i == 3) ? 0.70710678f : -0.70710678f, dy = i < 2 ? 0.70710678f : -0.70710678f;
                    float along = Mathf.Clamp(px * dx + py * dy, r1, r2);
                    float qx = px - dx * along, qy = py - dy * along;
                    best = Mathf.Min(best, Mathf.Sqrt(qx * qx + qy * qy));
                }
                float a = Mathf.Clamp01(halfWidth - best + 0.5f);
                t.SetPixel(x, y, new Color(1, 1, 1, a));
            }
        t.Apply();
        return t;
    }

    internal static void Draw(Vector2 vs, Camera cam)
    {
        float now = Time.unscaledTime;
        Markers.RemoveAll(m => now - m.T > MarkerLife);
        Labels.RemoveAll(l => now - l.T > LabelLife);
        if (Markers.Count > 0) DrawMarker(Markers[Markers.Count - 1], vs, now);
        if (Labels.Count == 0) return;

        float size = Mathf.Clamp(Plugin.FtSize.Value, 0.5f, 3f);
        int stack = 0;
        foreach (var l in Labels)
        {
            float t = (now - l.T) / LabelLife;
            float ease = 1f - (1f - t) * (1f - t);
            float a = t < 0.12f ? t / 0.12f : t < 0.6f ? 1f : Mathf.Clamp01(1f - (t - 0.6f) / 0.4f);
            Vector2 p;
            if (l.HasWorld && cam != null)
            {
                var sp = cam.WorldToScreenPoint(l.World);
                p = sp.z > 0.1f ? new Vector2(sp.x / Ui.Scale, (Screen.height - sp.y) / Ui.Scale) : new Vector2(vs.x / 2, vs.y * 0.36f);
            }
            else p = new Vector2(vs.x / 2, vs.y * 0.36f - stack * 26);
            stack++;
            var st = Ui.Text(Mathf.RoundToInt(19 * size), true, TextAnchor.MiddleCenter);
            var r = new Rect(p.x - 300 + l.Jitter * ease, p.y - 18 - 46 * ease, 600, 36);
            var old = GUI.color;
            GUI.color = new Color(0.06f, 0.04f, 0.02f, a * 0.75f);                     // soft drop shadow
            GUI.Label(new Rect(r.x + 1.2f, r.y + 1.6f, r.width, r.height), l.Text, st);
            GUI.Label(new Rect(r.x - 0.8f, r.y + 0.8f, r.width, r.height), l.Text, st);
            GUI.color = new Color(l.C.r, l.C.g, l.C.b, a);
            GUI.Label(r, l.Text, st);
            GUI.color = old;
        }
    }

    private static void DrawMarker(Marker m, Vector2 vs, float now)
    {
        if (_mark == null) _mark = MarkTex(1.6f);
        if (_rim == null) _rim = MarkTex(3.4f);
        float t = (now - m.T) / MarkerLife;
        float a = t < 0.45f ? 1f : Mathf.Clamp01(1f - (t - 0.45f) / 0.55f);
        float pop = t < 0.07f ? Mathf.Lerp(1.25f, 1f, t / 0.07f) : 1f;
        float size = 30f * Mathf.Clamp(Plugin.HitmarkerSize.Value, 0.5f, 3f) * pop * (m.Crit && m.Weak ? 1.18f : m.Crit ? 1.08f : 1f);
        var rect = new Rect(vs.x / 2f - size / 2f, vs.y / 2f - size / 2f, size, size);
        var old = GUI.color;
        Color core = m.Crit ? Crimson : m.Weak ? Amber : Ivory;
        if (m.Crit && m.Weak)
        {
            GUI.color = new Color(Amber.r, Amber.g, Amber.b, a);                       // amber rim
            GUI.DrawTexture(rect, _rim);
        }
        else
        {
            GUI.color = new Color(0.05f, 0.03f, 0.02f, a * 0.45f);                    // faint dark rim for contrast
            GUI.DrawTexture(rect, _rim);
        }
        GUI.color = new Color(core.r, core.g, core.b, a);
        GUI.DrawTexture(rect, _mark);
        GUI.color = old;
    }
}
