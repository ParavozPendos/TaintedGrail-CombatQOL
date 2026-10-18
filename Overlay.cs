using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using UnityEngine;
using TGShared;

namespace CombatQOL;

// IMGUI overlay: settings panel with tabs (hidden until the panel key, auto-hidden while any game menu
// is open), optional status line, hit zone drawing.
// Hit zones are drawn as dots via GUI.DrawTexture: no HDRP materials and no GUI matrix rotation needed.
public class Overlay : MonoBehaviour
{
#if IL2CPP
    public Overlay(IntPtr ptr) : base(ptr) { }
#endif

    // ---------- hit zone data ----------
    private struct Box { public Vector3 C, H; public Quaternion Q; public float T; public bool Hero, Hit, EnvHit; }
    private struct HitMark { public Vector3 P; public float T; public bool Env; }
    private struct Ring { public Vector3 C, N; public float R, T; public bool Spell; }

    private static readonly List<Box> Boxes = new();
    private static readonly List<HitMark> Hits = new();
    private static readonly List<Ring> Rings = new();
    private const float BoxLife = 0.35f, HitLife = 1.2f, RingLife = 0.8f;
    private const int MaxBoxes = 300;

    internal static int AddBox(Vector3 c, Vector3 half, Quaternion q, bool hero)
    {
        if (Boxes.Count >= MaxBoxes) Boxes.RemoveRange(0, MaxBoxes / 3);
        Boxes.Add(new Box { C = c, H = half, Q = q, T = Time.unscaledTime, Hero = hero });
        return Boxes.Count - 1;
    }

    // character = hit a character (yellow), otherwise the environment (grey).
    internal static void MarkHit(int box, Vector3 point, bool character)
    {
        if (box >= 0 && box < Boxes.Count) { var b = Boxes[box]; if (character) b.Hit = true; else b.EnvHit = true; Boxes[box] = b; }
        if (point != Vector3.zero)
        {
            if (Hits.Count > 100) Hits.RemoveRange(0, 30);
            Hits.Add(new HitMark { P = point, T = Time.unscaledTime, Env = !character });
        }
    }

    // Wide projectile zone along the flight path (a ring every physics step).
    internal static void AddRing(Vector3 c, Vector3 dir, float r, bool spell)
    {
        if (Rings.Count > 200) Rings.RemoveRange(0, 60);
        Rings.Add(new Ring { C = c, N = dir, R = r, T = Time.unscaledTime, Spell = spell });
    }

    // ---------- panel state ----------
    internal static bool PanelOpen;
    internal static bool PanelVisible;      // open AND no game menu on top
    private static string _weapon;
    private static float _zone, _vanilla;
    private static string _toast;
    private static float _toastUntil;
    private static bool _toastChip;
    private static float _heroSince = -1f;
    private static bool _hintShown;

    internal static void SetWeapon(string name, float zone, float vanilla) { _weapon = name; _zone = zone; _vanilla = vanilla; }
    internal static void Toast(string text, float seconds, bool chip = false) { _toast = text; _toastUntil = Time.unscaledTime + seconds; _toastChip = chip; }

    private sealed class Row
    {
        public string Key; public ConfigEntryBase Entry; public float Step; public string Fmt = "0.00"; public ConfigEntry<bool> Parent; public bool Header, Note;
    }
    private sealed class Tab { public string Key; public List<Row> Rows = new(); }

    private static readonly List<Tab> Tabs = new();
    private static int _tab;
    // selection: -2 = master switch, -1 = tabs row, 0.. = row index in the current tab
    private static int _sel = 0;
    private static float _scroll;

    private static void Build()
    {
        if (Tabs.Count > 0) return;
        Tab t = null;
        void T(string key) { t = new Tab { Key = key }; Tabs.Add(t); }
        void H(string key) => t.Rows.Add(new Row { Key = key, Header = true });
        void N(string key) => t.Rows.Add(new Row { Key = key, Note = true });
        void P(string key, ConfigEntryBase e, float step, string fmt = "0.00", ConfigEntry<bool> parent = null) => t.Rows.Add(new Row { Key = key, Entry = e, Step = step, Fmt = fmt, Parent = parent });

        T("tab_melee");
        H("sec_reach");
        P("p_enabled", Plugin.Enabled, 1);
        P("p_len", Plugin.LengthMultiplier, 0.1f, "0.0", Plugin.Enabled);
        P("p_add", Plugin.AdditionalLength, 0.1f, "0.0", Plugin.Enabled);
        P("p_width", Plugin.WidthMultiplier, 0.1f, "0.0", Plugin.Enabled);
        P("p_walls", Plugin.IgnoreEnvironment, 1, parent: Plugin.Enabled);
        P("p_maxtargets", Plugin.MaxTargetsPerSwing, 1, "0", Plugin.Enabled);
        H("sec_anim");
        P("p_nostop_enemy", Plugin.NoHitStopEnemies, 1);
        P("p_nostop_wall", Plugin.NoHitStopWalls, 1);
        H("sec_aim");
        P("p_aim", Plugin.AimEnabled, 1);
        P("p_aimspeed", Plugin.AimSpeed, 20f, "0", Plugin.AimEnabled);
        P("p_aimpitch", Plugin.AimPitch, 1, parent: Plugin.AimEnabled);
        H("sec_magnet");
        P("p_magnet", Plugin.MagnetEnabled, 1);
        P("p_magspeed", Plugin.MagnetSpeed, 0.25f, "0.00", Plugin.MagnetEnabled);
        P("p_magmax", Plugin.MagnetMaxPerAttack, 0.1f, "0.0", Plugin.MagnetEnabled);
        P("p_maggap", Plugin.MagnetStopGap, 0.1f, "0.0", Plugin.MagnetEnabled);
        H("sec_target");
        P("p_range", Plugin.TargetRange, 0.25f, "0.00");
        P("p_capture", Plugin.CaptureWidth, 0.1f, "0.0");
        P("p_window", Plugin.AssistWindow, 0.05f, "0.00");
        P("p_los", Plugin.RequireLineOfSight, 1);

        T("tab_ranged");
        H("sec_arrows");
        P("p_wide_arrows", Plugin.WideArrows, 1);
        P("p_arrow_add", Plugin.ArrowExtraRadius, 0.05f, "0.00", Plugin.WideArrows);
        P("p_thrown", Plugin.WideThrown, 1, parent: Plugin.WideArrows);
        H("sec_crit");
        P("p_crit", Plugin.CritCheck, 1, parent: Plugin.WideArrows);
        P("p_crit_width", Plugin.CritProbePercent, 5f, "0", Plugin.CritCheck);
        P("p_crit_look", Plugin.CritLookahead, 0.02f, "0.00", Plugin.CritCheck);
        N("sec_aimhelp");
        H("sec_hitmarker");
        P("p_hm", Plugin.BowHitmarker, 1);
        P("p_normal", Plugin.BowHmNormal, 1, parent: Plugin.BowHitmarker);
        P("p_weaks", Plugin.BowHmWeak, 1, parent: Plugin.BowHitmarker);
        P("p_crits", Plugin.BowHmCrit, 1, parent: Plugin.BowHitmarker);

        T("tab_magic");
        H("sec_spells");
        P("p_wide_spells", Plugin.WideSpells, 1);
        P("p_arrow_add", Plugin.SpellExtraRadius, 0.05f, "0.00", Plugin.WideSpells);
        N("sec_aimhelp");
        H("sec_hitmarker");
        P("p_hm", Plugin.MagicHitmarker, 1);
        P("p_normal", Plugin.MagicHmNormal, 1, parent: Plugin.MagicHitmarker);
        P("p_crits", Plugin.MagicHmCrit, 1, parent: Plugin.MagicHitmarker);

        T("tab_general");
        H("sec_ftext");
        P("p_ft", Plugin.FloatText, 1);
        P("p_crits", Plugin.FtCrits, 1, parent: Plugin.FloatText);
        P("p_weaks", Plugin.FtWeak, 1, parent: Plugin.FloatText);
        P("tab_melee", Plugin.FtMelee, 1, parent: Plugin.FloatText);
        P("tab_ranged", Plugin.FtBows, 1, parent: Plugin.FloatText);
        P("tab_magic", Plugin.FtMagic, 1, parent: Plugin.FloatText);
        P("p_ft_size", Plugin.FtSize, 0.1f, "0.0", Plugin.FloatText);
        H("sec_hitmarker");
        P("p_hm_size", Plugin.HitmarkerSize, 0.1f, "0.0");
        H("sec_display");
        P("p_hitboxes", Plugin.ShowHitboxes, 1);
        P("p_hud", Plugin.ShowHud, 1);
        P("p_scale", Plugin.UiScale, 0.1f, "0.0");
        P("p_console", Plugin.ShowConsole, 1);
    }

    private static List<Row> Rows => Tabs[_tab].Rows;
    private static bool Selectable(Row r) => !r.Header && !r.Note;

    private static string ValueText(ConfigEntryBase e, string fmt)
    {
        var v = e.BoxedValue;
        return v switch
        {
            bool b => Loc.T(b ? "on" : "off"),
            float f => f.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture),
            int i => i.ToString(),
            _ => v?.ToString() ?? ""
        };
    }

    private static void Move(int dir)
    {
        var rows = Rows;
        int i = _sel;
        while (true)
        {
            i += dir;
            if (i < -2) { i = -2; break; }
            if (i < 0) break;                         // master switch / tabs row
            if (i >= rows.Count) { i = _sel; break; }
            if (Selectable(rows[i])) break;
        }
        _sel = i;
    }

    private static void SwitchTab(int dir)
    {
        _tab = (_tab + dir + Tabs.Count) % Tabs.Count;
        _scroll = 0;
        if (_sel >= 0) { _sel = -1; Move(+1); }
    }

    private static void Change(ConfigEntryBase e, float step, int dir)
    {
        Plugin.IgnoreWatcherUntil = DateTime.UtcNow.AddSeconds(1.5);
        switch (e.BoxedValue)
        {
            case bool b: e.BoxedValue = !b; break;
            case int iv:
                int ni = iv + dir;
                if (e.Description.AcceptableValues is AcceptableValueRange<int> ri) ni = Math.Clamp(ni, ri.MinValue, ri.MaxValue);
                e.BoxedValue = ni; break;
            case float f:
                float nv = (float)Math.Round(f + dir * step, 4);
                if (e.Description.AcceptableValues is AcceptableValueRange<float> r) nv = Mathf.Clamp(nv, r.MinValue, r.MaxValue);
                e.BoxedValue = nv; break;
        }
        Patches.Logged.Clear();
        Plugin.L.LogInfo($"[panel] {e.Definition.Section}.{e.Definition.Key} = {e.BoxedValue}");
    }

    private static void Activate(int dir)
    {
        if (_sel == -2) { Change(Plugin.ModEnabled, 1, dir); return; }
        if (_sel == -1) { SwitchTab(dir); return; }
        var r = Rows[_sel];
        if (Selectable(r)) Change(r.Entry, r.Step, dir);
    }

    private static void Reset()
    {
        ConfigEntryBase e = _sel == -2 ? Plugin.ModEnabled : _sel >= 0 && Selectable(Rows[_sel]) ? Rows[_sel].Entry : null;
        if (e == null) return;
        Plugin.IgnoreWatcherUntil = DateTime.UtcNow.AddSeconds(1.5);
        e.BoxedValue = e.DefaultValue;
        Patches.Logged.Clear();
        Plugin.L.LogInfo($"[panel] {e.Definition.Key} reset to {e.DefaultValue}");
    }

    // ---------- keys ----------
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    private static readonly uint MyPid = (uint)Process.GetCurrentProcess().Id;

    internal static int Vk(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return 0;
        key = key.Trim().ToUpperInvariant();
        switch (key)
        {
            case "PAGEUP": return 0x21; case "PAGEDOWN": return 0x22; case "END": return 0x23; case "HOME": return 0x24;
            case "LEFT": return 0x25; case "UP": return 0x26; case "RIGHT": return 0x27; case "DOWN": return 0x28;
            case "INSERT": return 0x2D; case "DELETE": case "DEL": return 0x2E; case "BACKSPACE": return 0x08;
            case "ENTER": case "RETURN": return 0x0D;
            case "MINUS": return 0xBD; case "EQUALS": case "PLUS": return 0xBB;
            case "LEFTBRACKET": return 0xDB; case "RIGHTBRACKET": return 0xDD;
            case "ADD": return 0x6B; case "SUBTRACT": return 0x6D;
        }
        if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key.Substring(1), out var n) && n is >= 1 and <= 24) return 0x6F + n;
        if (key.StartsWith("NUMPAD") && int.TryParse(key.Substring(6), out var d) && d is >= 0 and <= 9) return 0x60 + d;
        if (key.Length == 1 && char.IsLetterOrDigit(key[0])) return key[0];
        return 0;
    }

    private static readonly Dictionary<int, bool> Prev = new();
    private static readonly Dictionary<int, float> RepeatAt = new();

    // Key press with auto-repeat while held.
    private static bool Pressed(int vk, bool repeat = false)
    {
        if (vk == 0) return false;
        bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
        Prev.TryGetValue(vk, out var was);
        Prev[vk] = down;
        if (down && !was) { RepeatAt[vk] = Time.unscaledTime + 0.4f; return true; }
        if (repeat && down && RepeatAt.TryGetValue(vk, out var t) && Time.unscaledTime >= t) { RepeatAt[vk] = Time.unscaledTime + 0.07f; return true; }
        return false;
    }

    private static bool Focused()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return pid == MyPid;
    }

    internal static string KeyLabel(string k)
    {
        if (string.IsNullOrWhiteSpace(k)) return "—";
        switch (k.Trim().ToUpperInvariant())
        {
            case "PAGEUP": return "PgUp"; case "PAGEDOWN": return "PgDn"; case "EQUALS": case "PLUS": return "=";
            case "MINUS": return "-"; case "DELETE": return "Del"; case "INSERT": return "Ins"; case "BACKSPACE": return "Bksp";
            case "LEFTBRACKET": return "["; case "RIGHTBRACKET": return "]"; case "ENTER": case "RETURN": return "Enter";
            case "UP": return "↑"; case "DOWN": return "↓"; case "LEFT": return "←"; case "RIGHT": return "→";
        }
        return k.Trim();
    }

    // Gameplay = cursor captured by the camera. Inventory, pause menu, map, dialogues free the cursor.
    private static bool InGameplay()
    {
        try { return Cursor.lockState == CursorLockMode.Locked && Awaken.TG.Main.Heroes.Hero.Current != null; } catch { return false; }
    }

    public void Update()
    {
        try
        {
            float now = Time.unscaledTime;
            Loc.Tick(now);
            Assist.Tick();
            Build();

            bool gameplay = InGameplay();
            if (!gameplay) _heroSince = -1f;
            else if (_heroSince < 0) _heroSince = now;
            if (!_hintShown && gameplay && now - _heroSince > 4f)
            {
                _hintShown = true;
                if (Plugin.ShowStartupHint.Value && Vk(Plugin.KeyPanel.Value) != 0) Toast(Loc.F("first_hint", "\u0001"), 7f, true);
            }

            PanelVisible = PanelOpen && gameplay;
            if (!Focused()) return;
            if (gameplay)
            {
                if (Pressed(Vk(Plugin.KeyHitboxes.Value))) { Plugin.IgnoreWatcherUntil = DateTime.UtcNow.AddSeconds(1.5); Plugin.ShowHitboxes.Value = !Plugin.ShowHitboxes.Value; }
                if (Pressed(Vk(Plugin.KeyToggle.Value)))
                {
                    Plugin.IgnoreWatcherUntil = DateTime.UtcNow.AddSeconds(1.5);
                    Plugin.ModEnabled.Value = !Plugin.ModEnabled.Value;
                    Patches.Logged.Clear();
                    Toast("CombatQOL: " + Loc.T(Plugin.ModEnabled.Value ? "on" : "off"), 2.5f);
                }
                if (Pressed(Vk(Plugin.KeyPanel.Value))) { PanelOpen = !PanelOpen; if (PanelOpen) _toastUntil = 0; }
            }
            PanelVisible = PanelOpen && gameplay;
            if (!PanelVisible) return;                       // keys only act while the panel is on screen

            if (_sel >= Rows.Count || (_sel >= 0 && !Selectable(Rows[_sel]))) { _sel = -1; Move(+1); }
            if (Pressed(Vk(Plugin.KeyUp.Value), true)) Move(-1);
            if (Pressed(Vk(Plugin.KeyDown.Value), true)) Move(+1);
            if (Pressed(Vk(Plugin.KeyTabPrev.Value), true)) SwitchTab(-1);
            if (Pressed(Vk(Plugin.KeyTabNext.Value), true)) SwitchTab(+1);
            bool inc = Pressed(Vk(Plugin.KeyRight.Value), true) | Pressed(Vk(Plugin.KeyInc.Value), true) | Pressed(0x6B, true) | Pressed(Vk(Plugin.KeyEnter.Value), true);
            bool dec = Pressed(Vk(Plugin.KeyLeft.Value), true) | Pressed(Vk(Plugin.KeyDec.Value), true) | Pressed(0x6D, true);
            if (inc) Activate(+1);
            else if (dec) Activate(-1);
            if (Pressed(Vk(Plugin.KeyReset.Value))) Reset();
        }
        catch (Exception e) { Plugin.L.LogError("Overlay.Update: " + e.Message); }
    }

    // ---------- drawing ----------
    private static Camera Cam()
    {
        var c = Camera.main;
        if (c != null) return c;
        var all = Camera.allCameras;
        return all != null && all.Length > 0 ? all[0] : null;
    }

    private static readonly int[,] Edges = { {0,1},{1,3},{3,2},{2,0}, {4,5},{5,7},{7,6},{6,4}, {0,4},{1,5},{2,6},{3,7} };
    private static readonly Vector3[] Pts = new Vector3[8];

    public void OnGUI()
    {
        try
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            float now = Time.unscaledTime;

            var vs = Ui.Begin(Plugin.UiScale.Value);
            try
            {
                if (InGameplay()) HitFeedback.Draw(vs, Cam());
                if (PanelVisible) DrawPanel(vs);
                else if (InGameplay())
                {
                    if (Plugin.ShowHud.Value) DrawHud();
                    if (_toast != null && now < _toastUntil) DrawToast(now);
                }
            }
            finally { Ui.End(); }

            if (!Plugin.ShowHitboxes.Value) { Boxes.Clear(); Hits.Clear(); Rings.Clear(); return; }
            DrawHitboxes(now);
        }
        catch (Exception e) { Plugin.L.LogError("Overlay.OnGUI: " + e.Message); Plugin.ShowHitboxes.Value = false; PanelOpen = false; }
    }

    private static void DrawHitboxes(float now)
    {
        var cam = Cam();
        if (cam == null) return;
        float sh = Screen.height;
        var tex = Texture2D.whiteTexture;
        Boxes.RemoveAll(b => now - b.T > BoxLife);
        Hits.RemoveAll(h => now - h.T > HitLife);
        Rings.RemoveAll(r => now - r.T > RingLife);

        foreach (var b in Boxes)
        {
            float a = 1f - (now - b.T) / BoxLife;
            GUI.color = b.Hit ? new Color(1f, 0.9f, 0.1f, a) : b.EnvHit ? new Color(0.6f, 0.6f, 0.6f, a) : b.Hero ? new Color(0.2f, 1f, 0.3f, a) : new Color(1f, 0.25f, 0.2f, a);
            bool ok = true;
            for (int i = 0; i < 8; i++)
            {
                var local = new Vector3((i & 1) == 0 ? -b.H.x : b.H.x, (i & 2) == 0 ? -b.H.y : b.H.y, (i & 4) == 0 ? -b.H.z : b.H.z);
                var sp = cam.WorldToScreenPoint(b.C + b.Q * local);
                if (sp.z <= 0.05f) { ok = false; break; }
                Pts[i] = new Vector3(sp.x, sh - sp.y, 0);
            }
            if (!ok) continue;
            for (int e = 0; e < 12; e++) DottedLine(Pts[Edges[e, 0]], Pts[Edges[e, 1]], tex);
        }
        foreach (var h in Hits)
        {
            var sp = cam.WorldToScreenPoint(h.P);
            if (sp.z <= 0.05f) continue;
            float ha = 1f - (now - h.T) / HitLife;
            GUI.color = h.Env ? new Color(0.6f, 0.6f, 0.6f, ha) : new Color(1f, 0.9f, 0.1f, ha);
            GUI.DrawTexture(new Rect(sp.x - 5, sh - sp.y - 5, 10, 10), tex);
        }
        foreach (var r in Rings)
        {
            float a = 1f - (now - r.T) / RingLife;
            GUI.color = r.Spell ? new Color(0.55f, 0.6f, 1f, a) : new Color(0.3f, 0.95f, 1f, a);
            var u = Vector3.Cross(r.N, Vector3.up); if (u.sqrMagnitude < 0.01f) u = Vector3.Cross(r.N, Vector3.right);
            u.Normalize();
            var w = Vector3.Cross(r.N, u);
            for (int i = 0; i < 16; i++)
            {
                float ang = i * Mathf.PI / 8f;
                var sp = cam.WorldToScreenPoint(r.C + (u * Mathf.Cos(ang) + w * Mathf.Sin(ang)) * r.R);
                if (sp.z <= 0.05f) continue;
                GUI.DrawTexture(new Rect(sp.x - 1, sh - sp.y - 1, 2, 2), tex);
            }
        }
        GUI.color = Color.white;
    }

    private static string HudText()
    {
        string reach = Plugin.ReachOn ? $"×{Plugin.LengthMultiplier.Value:0.0} +{Plugin.AdditionalLength.Value:0.0}" : Loc.T("off");
        string aim = Loc.T(Plugin.On && Plugin.AimEnabled.Value ? "on" : "off");
        string arrows = Loc.T(Plugin.On && Plugin.WideArrows.Value ? "on" : "off");
        string spells = Loc.T(Plugin.On && Plugin.WideSpells.Value ? "on" : "off");
        return $"{Loc.T("sec_reach")} {reach}   ·   {Loc.T("sec_aim")} {aim}   ·   {Loc.T("tab_ranged")} {arrows}   ·   {Loc.T("tab_magic")} {spells}";
    }

    private static void DrawHud()
    {
        var st = Ui.Text(14);
        var title = Ui.Text(14, true);
        string t = HudText();
        float w = Ui.Width("CombatQOL", title) + 14 + Ui.Width(t, st) + 28;
        var r = new Rect(14, 12, w, 30);
        Ui.Panel(r, 0.72f);
        Ui.Label(new Rect(r.x + 14, r.y, 200, r.height), "CombatQOL", title, Plugin.On ? Ui.Gold : Ui.Off);
        Ui.Label(new Rect(r.x + 14 + Ui.Width("CombatQOL", title) + 14, r.y, w, r.height), t, st, Ui.TextC);
        if (Assist.Disabled) Ui.Label(new Rect(r.x + 14, r.yMax + 2, 900, 24), Loc.T("st_error"), Ui.Text(13), Ui.Bad);
    }

    private static void DrawToast(float now)
    {
        float a = Mathf.Clamp01((_toastUntil - now) / 0.6f) * Mathf.Clamp01(1f - (_toastUntil - now - 6.6f) / 0.4f);
        var st = Ui.Text(16);
        string key = KeyLabel(Plugin.KeyPanel.Value);
        string[] parts = _toastChip ? _toast.Split('\u0001') : new[] { _toast };
        float chipW = _toastChip ? Ui.Width(key, Ui.Text(15, true)) + 12 : 0;
        float w = 28;
        foreach (var p in parts) w += Ui.Width(p, st);
        if (_toastChip) w += (chipW + 8) * (parts.Length - 1);
        var r = new Rect(14, 12, w, 34);
        var old = GUI.color;
        GUI.color = new Color(1, 1, 1, a);
        Ui.Panel(r, 0.80f * a);
        float x = r.x + 14;
        for (int i = 0; i < parts.Length; i++)
        {
            Ui.Label(new Rect(x, r.y, 900, r.height), parts[i], st, new Color(Ui.TextC.r, Ui.TextC.g, Ui.TextC.b, a));
            x += Ui.Width(parts[i], st);
            if (_toastChip && i < parts.Length - 1) { x += 4; x += Ui.Chip(x, r.y + 6, 22, key, 15) + 4; }
        }
        GUI.color = old;
    }

    private static GUIStyle _noteSt;
    private static bool _noteCjk;

    private static float RowHeight(Row r) => r.Header ? 30 : r.Note ? 46 : 27;

    private static void DrawPanel(Vector2 vs)
    {
        const float W = 600, pad = 18;
        var hintItems = new[]
        {
            ($"{KeyLabel(Plugin.KeyUp.Value)}{KeyLabel(Plugin.KeyDown.Value)}", Loc.T("h_select")),
            ($"{KeyLabel(Plugin.KeyLeft.Value)}{KeyLabel(Plugin.KeyRight.Value)}", Loc.T("h_change")),
            ($"{KeyLabel(Plugin.KeyTabPrev.Value)}/{KeyLabel(Plugin.KeyTabNext.Value)}", Loc.T("h_tabs")),
            (KeyLabel(Plugin.KeyReset.Value), Loc.T("h_reset")),
            (KeyLabel(Plugin.KeyPanel.Value), Loc.T("h_close")),
        };
        float width = Mathf.Max(W, Ui.HintsWidth(13, hintItems) + pad * 2);
        float x0 = 28, y0 = 90;
        // Bottom limit keeps the panel clear of the health/mana/stamina bars in the bottom-left corner.
        float maxBottom = vs.y - 210;
        const float headH = 48 + 30 + 38 + 8, statusH = 8 + 6 + 22 + 22 + 12, footH = 34;
        var rows = Rows;
        float content = 0;
        foreach (var r in rows) content += RowHeight(r);
        float listH = Mathf.Min(content, Mathf.Max(120, maxBottom - y0 - headH - statusH - footH));
        float h = headH + listH + statusH + footH;
        var rect = new Rect(x0, y0, width, h);
        Ui.Panel(rect, 0.965f);

        // title
        var title = Ui.Text(22, true);
        Ui.Label(new Rect(x0 + pad, y0 + 8, 300, 34), "CombatQOL", title, Ui.Gold);
        float tw = Ui.Width("CombatQOL", title);
        Ui.Label(new Rect(x0 + pad + tw + 12, y0 + 10, 400, 34), Loc.T("title_sub"), Ui.Text(15), Ui.Muted);
        Ui.Label(new Rect(x0 + width - pad - 80, y0 + 10, 80, 34), "v" + Plugin.Version, Ui.Text(12, false, TextAnchor.MiddleRight), Ui.Off);
        float y = y0 + 48;

        // master switch
        var nameSt = Ui.Text(15);
        var valSt = Ui.Text(15, true, TextAnchor.MiddleRight);
        DrawRow(x0, y, width, pad, 28, Loc.T("p_master"), Plugin.ModEnabled, "0", _sel == -2, false, true);
        y += 30;

        // tabs
        var tabSt = Ui.Text(15, true, TextAnchor.MiddleCenter);
        float tx = x0 + pad;
        bool tabsSel = _sel == -1;
        if (tabsSel) Ui.Rect(new Rect(x0 + 8, y + 2, width - 16, 32), new Color(Ui.Gold.r, Ui.Gold.g, Ui.Gold.b, 0.08f));
        for (int i = 0; i < Tabs.Count; i++)
        {
            string label = Loc.T(Tabs[i].Key);
            float w = Ui.Width(label, tabSt) + 26;
            bool active = i == _tab;
            var tr = new Rect(tx, y + 4, w, 28);
            if (active) Ui.Rect(new Rect(tr.x, tr.yMax - 2, tr.width, 2), Ui.Gold);
            Ui.Label(tr, label, tabSt, active ? (tabsSel ? Color.white : Ui.Gold) : Ui.Muted);
            tx += w + 6;
        }
        if (tabsSel) Ui.Rect(new Rect(x0 + 8, y + 3, 3, 30), Ui.Gold);
        y += 38;
        Ui.Rect(new Rect(x0 + pad, y, width - pad * 2, 1), Ui.GoldDim);
        y += 8;

        // scrolled list
        float selTop = 0, selBottom = 0, acc = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            float rh = RowHeight(rows[i]);
            if (i == _sel) { selTop = acc; selBottom = acc + rh; }
            acc += rh;
        }
        if (_sel >= 0)
        {
            if (selTop < _scroll) _scroll = selTop - (_sel <= 1 ? 30 : 0);
            if (selBottom > _scroll + listH) _scroll = selBottom - listH;
        }
        else _scroll = 0;
        _scroll = Mathf.Clamp(_scroll, 0, Mathf.Max(0, content - listH));

        var headSt = Ui.Text(13, true);
        if (_noteSt == null || _noteCjk != Loc.IsCjk) { _noteSt = new GUIStyle(Ui.Text(13)) { wordWrap = true }; _noteCjk = Loc.IsCjk; }
        var noteSt = _noteSt;
        float ly = y - _scroll;
        GUI.BeginGroup(new Rect(0, y, vs.x, listH));
        try
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                float rh = RowHeight(r);
                float ry = ly - y;
                if (ry + rh >= 0 && ry <= listH)
                {
                    if (r.Header) Ui.Label(new Rect(x0 + pad, ry + 6, width, rh - 6), Loc.T(r.Key).ToUpperInvariant(), headSt, Ui.GoldDim);
                    else if (r.Note) Ui.Label(new Rect(x0 + pad + 8, ry + 2, width - pad * 2 - 16, rh - 4), Loc.T(r.Key), noteSt, new Color(0.78f, 0.76f, 0.72f, 1f));
                    else
                    {
                        bool dim = !Plugin.On || (r.Parent != null && !r.Parent.Value);
                        DrawRow(x0, ry, width, pad, rh, Loc.T(r.Key), r.Entry, r.Fmt, i == _sel, dim, false);
                    }
                }
                ly += rh;
            }
        }
        finally { GUI.EndGroup(); }

        // scrollbar
        if (content > listH + 1)
        {
            float trackX = x0 + width - 7;
            Ui.Rect(new Rect(trackX, y, 3, listH), new Color(1, 1, 1, 0.06f));
            float th = Mathf.Max(24, listH * listH / content);
            float tyy = y + (listH - th) * (_scroll / Mathf.Max(1, content - listH));
            Ui.Rect(new Rect(trackX, tyy, 3, th), Ui.GoldDim);
        }
        y += listH;

        // live status for the current tab
        y += 8;
        Ui.Rect(new Rect(x0 + pad, y, width - pad * 2, 1), new Color(1, 1, 1, 0.08f));
        y += 6;
        var small = Ui.Text(14);
        string l1, l2 = null;
        Color c2 = Ui.Muted;
        switch (Tabs[_tab].Key)
        {
            case "tab_melee":
                l1 = _weapon != null ? Loc.F("st_weapon", _weapon, _zone, _vanilla) : Loc.T("st_noweapon");
                l2 = Assist.Disabled ? Loc.T("st_error") : (Assist.Status.Length > 0 ? Assist.Status : Loc.T("st_notarget"));
                if (Assist.Disabled) c2 = Ui.Bad;
                break;
            case "tab_ranged":
            case "tab_magic":
                l1 = Projectiles.LastVanilla > 0 ? Loc.F("st_arrow", Projectiles.LastVanilla, Projectiles.LastWide, Projectiles.Nudges) : Loc.T("st_noshot");
                if (Projectiles.Disabled) { l2 = Loc.T("st_error"); c2 = Ui.Bad; }
                break;
            default:
                l1 = "";
                break;
        }
        Ui.Label(new Rect(x0 + pad, y, width - pad * 2, 22), l1, small, Color.white);
        y += 22;
        if (l2 != null) Ui.Label(new Rect(x0 + pad, y, width - pad * 2, 22), l2, Ui.Text(13), c2);
        y += 22 + 12;

        Ui.Hints(x0 + pad, y, 24, 13, hintItems);
    }

    private static void DrawRow(float x0, float y, float width, float pad, float rh, string name, ConfigEntryBase e, string fmt, bool sel, bool dim, bool master)
    {
        if (sel)
        {
            Ui.Rect(new Rect(x0 + 8, y + 1, width - 16, rh - 2), new Color(Ui.Gold.r, Ui.Gold.g, Ui.Gold.b, 0.13f));
            Ui.Rect(new Rect(x0 + 8, y + 1, 3, rh - 2), Ui.Gold);
        }
        var nc = sel ? Color.white : Ui.TextC;
        if (dim) nc = new Color(nc.r, nc.g, nc.b, 0.45f);
        Ui.Label(new Rect(x0 + pad + 8, y, width - 200, rh), name, master ? Ui.Text(15, true) : Ui.Text(15), master ? (sel ? Color.white : Ui.Gold) : nc);
        string v = ValueText(e, fmt);
        Color vc = e.BoxedValue is bool b ? (b ? Ui.On : Ui.Off) : (sel ? Color.white : Ui.TextC);
        if (dim) vc = new Color(vc.r, vc.g, vc.b, 0.45f);
        if (sel) v = "‹  " + v + "  ›";
        Ui.Label(new Rect(x0 + width - pad - 200, y, 200, rh), v, Ui.Text(15, true, TextAnchor.MiddleRight), vc);
    }

    private static void DottedLine(Vector3 a, Vector3 b, Texture tex)
    {
        float len = Vector2.Distance(new Vector2(a.x, a.y), new Vector2(b.x, b.y));
        int n = Mathf.Clamp((int)(len / 3f), 1, 300);
        for (int i = 0; i <= n; i++)
        {
            float t = (float)i / n;
            GUI.DrawTexture(new Rect(a.x + (b.x - a.x) * t - 1, a.y + (b.y - a.y) * t - 1, 2, 2), tex);
        }
    }
}
