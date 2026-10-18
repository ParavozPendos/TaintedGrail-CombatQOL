using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
#if IL2CPP
using BepInEx.Unity.IL2CPP;
#endif
using HarmonyLib;
using Awaken.TG.Main.Animations.FSM.Heroes.Base;
using Awaken.TG.Main.Animations.FSM.Heroes.Machines;
using Awaken.TG.Main.Heroes.Combat;
using UnityEngine;
using TGShared;

namespace CombatQOL;

[BepInPlugin(Guid, "CombatQOL", Version)]
public class Plugin :
#if IL2CPP
    BasePlugin
#else
    BaseUnityPlugin
#endif
{
    public const string Guid = "leon.tg.combatqol";
    public const string Version = "1.0.0";

    internal static ManualLogSource L;
    internal static ConfigFile Cfg;

    // [General]
    internal static ConfigEntry<bool> ModEnabled;
    internal static ConfigEntry<string> Language;
    internal static ConfigEntry<bool> ShowConsole;
    // [Hitmarkers] / [CombatText]
    internal static ConfigEntry<bool> BowHitmarker, BowHmNormal, BowHmWeak, BowHmCrit, MagicHitmarker, MagicHmNormal, MagicHmCrit;
    internal static ConfigEntry<float> HitmarkerSize, FtSize;
    internal static ConfigEntry<bool> FloatText, FtCrits, FtWeak, FtMelee, FtBows, FtMagic;
    // [Reach]
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<float> LengthMultiplier;
    internal static ConfigEntry<float> AdditionalLength;
    internal static ConfigEntry<float> WidthMultiplier;
    internal static ConfigEntry<bool> ApplyToNpcs;
    internal static ConfigEntry<bool> IgnoreEnvironment;
    internal static ConfigEntry<int> MaxTargetsPerSwing;
    // [Animation]
    internal static ConfigEntry<bool> NoHitStopEnemies;
    internal static ConfigEntry<bool> NoHitStopWalls;
    // [Target]
    internal static ConfigEntry<float> TargetRange;
    internal static ConfigEntry<float> CaptureWidth;
    internal static ConfigEntry<float> AssistWindow;
    internal static ConfigEntry<bool> RequireLineOfSight;
    // [AimAssist]
    internal static ConfigEntry<bool> AimEnabled;
    internal static ConfigEntry<float> AimSpeed;
    internal static ConfigEntry<bool> AimPitch;
    // [Magnet]
    internal static ConfigEntry<bool> MagnetEnabled;
    internal static ConfigEntry<float> MagnetSpeed;
    internal static ConfigEntry<float> MagnetMaxPerAttack;
    internal static ConfigEntry<float> MagnetStopGap;
    // [Ranged]
    internal static ConfigEntry<bool> WideArrows, WideThrown, CritCheck;
    internal static ConfigEntry<float> ArrowExtraRadius, CritProbePercent, CritLookahead;
    // [Magic]
    internal static ConfigEntry<bool> WideSpells;
    internal static ConfigEntry<float> SpellExtraRadius;
    // [Hud]
    internal static ConfigEntry<bool> ShowHud;
    internal static ConfigEntry<bool> ShowStartupHint;
    internal static ConfigEntry<float> UiScale;
    internal static ConfigEntry<bool> ShowHitboxes;
    // [Keys]
    internal static ConfigEntry<string> KeyPanel, KeyUp, KeyDown, KeyLeft, KeyRight, KeyEnter, KeyTabPrev, KeyTabNext, KeyInc, KeyDec, KeyReset, KeyHitboxes, KeyToggle;
    // [Debug]
    internal static ConfigEntry<bool> LogWeapons;
    internal static ConfigEntry<bool> LogHits;
    internal static ConfigEntry<bool> LogAssist;
#if DEBUG && IL2CPP
    internal static ConfigEntry<bool> Diagnostics;
    internal static ConfigEntry<string> DiagMethods;
    internal static ConfigEntry<string> DiagFieldScan;
#endif

    internal static bool On => ModEnabled.Value;
    internal static bool ReachOn => ModEnabled.Value && Enabled.Value;
    internal static float Mult => LengthMultiplier.Value;
    internal static float Add => AdditionalLength.Value;

    private FileSystemWatcher _watcher;
    internal static DateTime IgnoreWatcherUntil;

    private static ConfigDescription R(string d, float a, float b) => new(d, new AcceptableValueRange<float>(a, b));

#if IL2CPP
    public override void Load() => Init();
#else
    private void Awake() => Init();
    private ManualLogSource Log => Logger;
    private static T AddComponent<T>() where T : Component => Compat.Spawn<T>("CombatQOL");
#endif

    private void Init()
    {
        L = Log;
        Cfg = Config;
        Config.SaveOnConfigSet = true;

        ModEnabled = Config.Bind("General", "Enabled", true, "Master switch for the whole mod | Главный выключатель мода");
        Language = Config.Bind("General", "Language", "auto",
            "UI language: auto = same as the game, or en, ru, de, fr, es, it, pl, cs, pt, ja, ko, zh-Hans, zh-Hant, uk | Язык интерфейса: auto = как в игре");

        ShowConsole = Config.Bind("General", "ShowConsole", false,
            "Show the BepInEx console window (logs still go to BepInEx\\LogOutput.log). To never create it, set [Logging.Console] Enabled = false in BepInEx.cfg | Показывать окно консоли BepInEx (лог всё равно пишется в файл)");

        BowHitmarker = Config.Bind("Hitmarkers", "Bows", true, "Hitmarker at the crosshair for arrows and thrown weapons | Хитмаркер у прицела для стрел и метательного");
        BowHmNormal = Config.Bind("Hitmarkers", "BowsNormal", true, "Bows: normal hits (white) | Луки: обычные попадания (белый)");
        BowHmWeak = Config.Bind("Hitmarkers", "BowsWeakSpot", true, "Bows: weak spots (yellow) | Луки: уязвимые точки (жёлтый)");
        BowHmCrit = Config.Bind("Hitmarkers", "BowsCrit", true, "Bows: critical hits (red; crit on a weak spot = red with a yellow outline) | Луки: криты (красный)");
        MagicHitmarker = Config.Bind("Hitmarkers", "Magic", true, "Hitmarker for spell projectiles | Хитмаркер для магических снарядов");
        MagicHmNormal = Config.Bind("Hitmarkers", "MagicNormal", true, "Magic: normal hits (white) | Магия: обычные попадания (белый)");
        MagicHmCrit = Config.Bind("Hitmarkers", "MagicCrit", true, "Magic: critical hits (red) | Магия: криты (красный)");
        HitmarkerSize = Config.Bind("Hitmarkers", "Size", 1.0f, R("Hitmarker size | Размер хитмаркера", 0.5f, 3f));

        FloatText = Config.Bind("CombatText", "Enabled", true, "Floating \"Crit!\" / \"Weak spot!\" labels above the enemy | Всплывающие «Крит!» / «Уязвимая точка!» над врагом");
        FtCrits = Config.Bind("CombatText", "Crits", true, "Label critical hits | Подписывать криты");
        FtWeak = Config.Bind("CombatText", "WeakSpots", true, "Label weak spot hits | Подписывать уязвимые точки");
        FtMelee = Config.Bind("CombatText", "Melee", true, "For melee | Для ближнего боя");
        FtBows = Config.Bind("CombatText", "Bows", true, "For bows and thrown weapons | Для луков и метательного");
        FtMagic = Config.Bind("CombatText", "Magic", true, "For magic | Для магии");
        FtSize = Config.Bind("CombatText", "Size", 1.0f, R("Label size | Размер подписей", 0.5f, 3f));

        Enabled = Config.Bind("Reach", "Enabled", true, "Longer melee reach for the hero | Увеличенная дальность ближних атак героя");
        LengthMultiplier = Config.Bind("Reach", "LengthMultiplier", 3.0f, R("Hit zone length multiplier along the blade (1 = vanilla) | Множитель длины зоны удара вдоль клинка (1 = как в игре)", 0.2f, 20f));
        AdditionalLength = Config.Bind("Reach", "AdditionalLength", 1.0f, R("Metres added to the hit zone length (after the multiplier) | Сколько метров добавить к длине (после множителя)", 0f, 20f));
        WidthMultiplier = Config.Bind("Reach", "WidthMultiplier", 4.5f, R("Hit zone width/thickness multiplier | Множитель ширины/толщины зоны удара", 0.2f, 8f));
        ApplyToNpcs = Config.Bind("Reach", "ApplyToNpcs", false, "Also apply reach to NPC weapons | Применять дальность и к оружию NPC");
        IgnoreEnvironment = Config.Bind("Reach", "IgnoreEnvironment", true,
            "Hero swings are not stopped by walls, rocks and props | Удар героя не останавливается о стены, скалы и предметы");
        MaxTargetsPerSwing = Config.Bind("Reach", "MaxTargetsPerSwing", 2,
            new ConfigDescription("How many different enemies one swing can hit (0 = unlimited, vanilla) | Сколько разных врагов задевает один взмах (0 = без ограничения)", new AcceptableValueRange<int>(0, 10)));

        NoHitStopEnemies = Config.Bind("Animation", "NoHitStopOnEnemies", true,
            "Attack animation is not frozen/slowed when the weapon hits an enemy | Анимация удара не замирает при попадании по врагу");
        NoHitStopWalls = Config.Bind("Animation", "NoHitStopOnWalls", true,
            "Weapon does not bounce off walls and props: the swing always plays to the end | Оружие не отскакивает от стен: взмах всегда доигрывается");

        TargetRange = Config.Bind("Target", "Range", 4.0f, R("Enemies further than this are not picked, m (from enemy edge) | Дальше этого цель не выбирается, м", 0.5f, 15f));
        CaptureWidth = Config.Bind("Target", "CaptureWidth", 2.0f,
            R("How far sideways from the crosshair an enemy may be, m. Wide angle up close, narrow far away | Насколько вбок от прицела может быть враг, м", 0.1f, 5f));
        AssistWindow = Config.Bind("Target", "Window", 0.30f, R("Seconds after a swing starts during which aim assist and magnet work | Сколько секунд после начала удара работают доводка и прилипание", 0.05f, 2f));
        RequireLineOfSight = Config.Bind("Target", "RequireLineOfSight", true, "Do not pick targets behind walls | Не выбирать цель за стеной");

        AimEnabled = Config.Bind("AimAssist", "Enabled", true, "Smoothly turn the camera toward the enemy during a melee swing | Плавная доводка прицела к врагу во время удара");
        AimSpeed = Config.Bind("AimAssist", "Speed", 280f, R("Max camera turn speed toward the target, deg/s | Максимальная скорость поворота камеры к цели, °/с", 0f, 2000f));
        AimPitch = Config.Bind("AimAssist", "VerticalAssist", true, "Assist vertically too | Доводить и по вертикали");

        MagnetEnabled = Config.Bind("Magnet", "Enabled", true, "Gently pull the hero toward the enemy during a swing | Лёгкое подтягивание героя к врагу во время удара");
        MagnetSpeed = Config.Bind("Magnet", "Speed", 3.0f, R("Pull speed, m/s | Скорость подтягивания, м/с", 0f, 20f));
        MagnetMaxPerAttack = Config.Bind("Magnet", "MaxPerAttack", 1.0f, R("Max metres pulled per swing | Максимум метров за один удар", 0f, 5f));
        MagnetStopGap = Config.Bind("Magnet", "StopGap", 1.0f, R("Stop this far from the enemy edge, m | На каком расстоянии от края врага остановиться, м", 0f, 5f));

        WideArrows = Config.Bind("Ranged", "WideArrows", true,
            "Wider hit zone for the hero's arrows: near misses are nudged into a hostile in the zone (walls still block) | Широкая зона попадания стрел героя");
        ArrowExtraRadius = Config.Bind("Ranged", "ExtraRadius", 0.25f, R("Metres added to the arrow hit radius | Сколько метров добавить к радиусу стрелы", 0f, 50f));
        WideThrown = Config.Bind("Ranged", "ThrownWeapons", true, "Same for thrown weapons | То же для метательного оружия");
        CritCheck = Config.Bind("Ranged", "CritCheck", true,
            "Weak-spot check with a narrow arrow on the true path: a near miss becomes an ordinary body hit, never a headshot | Проверка уязвимой точки узкой стрелой по настоящей траектории");
        CritProbePercent = Config.Bind("Ranged", "CritArrowWidthPercent", 100f, R("Width of the narrow crit arrow, % of the game's arrow | Ширина узкой крит-стрелы, % от обычной", 10f, 100f));
        CritLookahead = Config.Bind("Ranged", "CritLookahead", 0.10f, R("How far ahead (seconds of flight) the narrow arrow checks for a real hit | Насколько вперёд (секунды полёта) проверяет узкая стрела", 0f, 0.5f));

        WideSpells = Config.Bind("Magic", "WideSpells", true, "Wider hit zone for the hero's spell projectiles | Широкая зона попадания магических снарядов героя");
        SpellExtraRadius = Config.Bind("Magic", "ExtraRadius", 0.30f, R("Metres added to the spell projectile radius | Сколько метров добавить к радиусу снаряда", 0f, 50f));

        ShowHud = Config.Bind("Hud", "ShowHud", false, "Small status line in the top-left corner | Строка состояния в левом верхнем углу");
        ShowStartupHint = Config.Bind("Hud", "ShowStartupHint", true, "Show a short hint with the panel key after loading a save | Подсказка с клавишей панели после загрузки");
        UiScale = Config.Bind("Hud", "UiScale", 1.0f, R("Panel size multiplier (scales with screen resolution automatically) | Размер панели (под разрешение подстраивается сам)", 0.6f, 2.5f));
        ShowHitboxes = Config.Bind("Hud", "ShowHitboxes", false, "Draw melee hit zones: green = hero, red = NPC, yellow = hit a character, grey = hit the environment | Рисовать зоны ударов");

        const string keyHelp = " Keys: F1..F24, A..Z, 0..9, Numpad0..9, PageUp, PageDown, Home, End, Insert, Delete, Backspace, Enter, Minus, Equals, LeftBracket, RightBracket, Up, Down, Left, Right; empty = none";
        KeyPanel = Config.Bind("Keys", "Panel", "F10", "Open/close the settings panel | Открыть/закрыть панель настроек." + keyHelp);
        KeyUp = Config.Bind("Keys", "Up", "Up", "Panel: previous row | Панель: строка выше");
        KeyDown = Config.Bind("Keys", "Down", "Down", "Panel: next row | Панель: строка ниже");
        KeyLeft = Config.Bind("Keys", "Left", "Left", "Panel: decrease / toggle / previous tab | Панель: уменьшить / переключить / вкладка левее");
        KeyRight = Config.Bind("Keys", "Right", "Right", "Panel: increase / toggle / next tab | Панель: увеличить / переключить / вкладка правее");
        KeyEnter = Config.Bind("Keys", "Enter", "Enter", "Panel: increase / toggle | Панель: увеличить / переключить");
        KeyTabPrev = Config.Bind("Keys", "PrevTab", "PageUp", "Panel: previous tab | Панель: предыдущая вкладка");
        KeyTabNext = Config.Bind("Keys", "NextTab", "PageDown", "Panel: next tab | Панель: следующая вкладка");
        KeyInc = Config.Bind("Keys", "Increase", "Equals", "Panel: increase (Numpad + works too) | Панель: увеличить");
        KeyDec = Config.Bind("Keys", "Decrease", "Minus", "Panel: decrease (Numpad - works too) | Панель: уменьшить");
        KeyReset = Config.Bind("Keys", "ResetParam", "Delete", "Panel: reset the selected setting to default | Панель: сбросить параметр по умолчанию");
        KeyHitboxes = Config.Bind("Keys", "ToggleHitboxes", "", "Optional: toggle hit zone drawing | Необязательно: показ зон ударов");
        KeyToggle = Config.Bind("Keys", "ToggleMod", "", "Optional: whole mod on/off, to compare with vanilla | Необязательно: весь мод вкл/выкл");

        LogWeapons = Config.Bind("Debug", "LogWeapons", false, "Log hit zone size of each hero weapon (once per weapon) | Писать в лог размер зоны удара оружия");
        LogHits = Config.Bind("Debug", "LogHits", false, "Log what the hit box touches (layer and object) | Писать в лог, во что попадает удар");
        LogAssist = Config.Bind("Debug", "LogAssist", false, "Log picked targets and pull distance | Писать в лог цель и подтягивание");
#if DEBUG && IL2CPP
        Diagnostics = Config.Bind("Debug", "Diagnostics", false, "Dev tool: disassemble methods at start (BepInEx\\CombatQOL_diag.txt)");
        DiagMethods = Config.Bind("Debug", "DiagMethods", "", "Type:method1,method2; Type2:method");
        DiagFieldScan = Config.Bind("Debug", "DiagFieldScan", "", "Type:field1,field2");
#endif

        Loc.Table = Strings.Build();
        Loc.Forced = () => Language.Value;
        Loc.Log = s => L.LogInfo(s);

        ConsoleWindow.Apply(ShowConsole.Value);
        ShowConsole.SettingChanged += (object sender, EventArgs args) => ConsoleWindow.Apply(ShowConsole.Value);
        WatchConfig();
#if DEBUG && IL2CPP
        if (Diagnostics.Value) Diag.Run(DiagMethods.Value, DiagFieldScan.Value); // before patches — original code
#endif
        try { new Harmony(Guid).PatchAll(typeof(Patches)); }
        catch (Exception e) { L.LogError("Patching failed, mod is inactive: " + e); return; }
        try { AddComponent<Overlay>(); }
        catch (Exception e) { L.LogWarning("Overlay disabled: " + e.Message); }
        L.LogInfo($"CombatQOL {Version} ({Compat.Runtime}): reach x{LengthMultiplier.Value} +{AdditionalLength.Value} m, width x{WidthMultiplier.Value}; aim {AimEnabled.Value}; magnet {MagnetEnabled.Value}; no hit-stop enemies/walls {NoHitStopEnemies.Value}/{NoHitStopWalls.Value}; arrows +{ArrowExtraRadius.Value} m (crit check {CritCheck.Value}), spells +{SpellExtraRadius.Value} m; panel [{KeyPanel.Value}]");
    }

    // Editing the .cfg while the game runs is picked up live.
    private void WatchConfig()
    {
        try
        {
            var path = Config.ConfigFilePath;
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(path), Path.GetFileName(path)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
            _watcher.Changed += (object sender, FileSystemEventArgs args) =>
            {
                if (DateTime.UtcNow < IgnoreWatcherUntil) return; // our own save from the panel
                try
                {
                    System.Threading.Thread.Sleep(150);
                    Config.Reload();
                    Patches.Logged.Clear();
                    L.LogInfo($"Config reloaded: reach x{LengthMultiplier.Value} +{AdditionalLength.Value} m, enabled={ModEnabled.Value}/{Enabled.Value}");
                }
                catch (Exception e) { L.LogWarning("Config reload failed: " + e.Message); }
            };
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) { L.LogWarning("Config watcher: " + e.Message); }
    }
}

[HarmonyPatch]
internal static class Patches
{
    internal static readonly HashSet<ObjId> Logged = new();
    // Оригинальный size оружия, пока идёт ProcessUpdate; если postfix не отработал — следующий prefix вернёт оригинал.
    private static readonly Dictionary<ObjId, Vector3> Pending = new();
    // Оружие, у которого на время ProcessUpdate снят флаг героя (IgnoreEnvironment).
    private static readonly HashSet<ObjId> PendingFlip = new();
    internal static CharacterWeapon HeroWeapon;
    internal static int TargetsThisSwing;
    private static readonly HashSet<string> LoggedHits = new();
    internal static float HeroWeaponSeen;

    // ProcessUpdate каждый кадр делает Physics.BoxCastNonAlloc от ColliderPivot вперёд вдоль клинка;
    // размер бокса — get_Size, который читает поле size. Подменяем size только на время вызова.
    [HarmonyPrefix, HarmonyPatch(typeof(CharacterWeapon), nameof(CharacterWeapon.ProcessUpdate))]
    private static void Prefix(CharacterWeapon __instance, out (bool mod, Vector3 size, int box, bool flip, float addHero) __state)
    {
        __state = (false, default, -1, false, float.NaN);
        try
        {
            if (Pending.Remove(__instance.Id(), out var stale)) __instance.size = stale;
            if (PendingFlip.Remove(__instance.Id())) __instance._attachedToHero = true;
            bool hero = __instance._attachedToHero;
            if (hero) { HeroWeapon = __instance; HeroWeaponSeen = Time.unscaledTime; }

            if (Plugin.ReachOn && (hero || Plugin.ApplyToNpcs.Value))
            {
                var s = __instance.size;
                float w = Plugin.WidthMultiplier.Value;
                var mod = new Vector3(s.x * w, s.y * w, s.z * Plugin.Mult + Plugin.Add);
                __state = (true, s, -1, false, float.NaN);
                Pending[__instance.Id()] = s;
                __instance.size = mod;
                if (Plugin.LogWeapons.Value && hero && Logged.Add(__instance.Id()))
                    Plugin.L.LogInfo($"[hero weapon] {__instance.gameObject.name}: size {s} -> {mod} (addHero={__instance.additionalLengthForHero})");
            }

            if (__instance._inAttack && (Plugin.ShowHitboxes.Value || (hero && (Plugin.ShowHud.Value || Overlay.PanelOpen))))
            {
                var S = __instance.Size; // ровно то, что возьмёт BoxCast в этом кадре
                if (hero)
                {
                    float baseZ = __state.mod ? __state.size.z : __instance.size.z;
                    Overlay.SetWeapon(ShortName(__instance.gameObject.name), S.z, S.z - __instance.size.z + baseZ);
                }
                if (Plugin.ShowHitboxes.Value)
                {
                    var pivot = __instance.ColliderPivot;
                    if (pivot != null)
                    {
                        int idx = Overlay.AddBox(pivot.position + pivot.forward * (S.z * 0.5f), S * 0.5f, pivot.rotation, hero);
                        __state.box = idx;
                        // очищаем буфер попаданий: после вызова непустые элементы — попадания этого кадра
                        var arr = __instance._hitResults;
                        if (arr != null) for (int i = 0; i < arr.Length; i++) arr[i] = default;
                    }
                }
            }
            // Лимит целей за взмах: набрали — схлопываем бокс до точки (длина героя тоже обнуляется на кадр).
            if (hero && Plugin.ReachOn && Plugin.MaxTargetsPerSwing.Value > 0 && TargetsThisSwing >= Plugin.MaxTargetsPerSwing.Value)
            {
                if (!__state.mod) { __state.mod = true; __state.size = __instance.size; Pending[__instance.Id()] = __state.size; }
                __state.addHero = __instance.additionalLengthForHero;
                __instance.additionalLengthForHero = 0f;
                __instance.size = new Vector3(0.0001f, 0.0001f, 0.0001f);
                return;
            }

            // Проверка «удар упёрся в окружение» в ProcessUpdate есть только у оружия героя (_attachedToHero).
            // Снимаем флаг на этот кадр, а длину бокса фиксируем такой же, какой её дал бы геттер героя.
            if (hero && Plugin.IgnoreEnvironment.Value && Plugin.ReachOn)
            {
                var heroSize = __instance.Size;
                if (!__state.mod) { __state.mod = true; __state.size = __instance.size; Pending[__instance.Id()] = __state.size; }
                __instance.size = heroSize;
                __instance._attachedToHero = false;
                PendingFlip.Add(__instance.Id());
                __state.flip = true;
            }

        }
        catch (Exception e) { Plugin.L.LogError("Prefix: " + e.Message); }
    }

    [HarmonyPostfix, HarmonyPatch(typeof(CharacterWeapon), nameof(CharacterWeapon.ProcessUpdate))]
    private static void Postfix(CharacterWeapon __instance, (bool mod, Vector3 size, int box, bool flip, float addHero) __state)
    {
        try
        {
            if (!float.IsNaN(__state.addHero)) __instance.additionalLengthForHero = __state.addHero;
            if (__state.flip) { __instance._attachedToHero = true; PendingFlip.Remove(__instance.Id()); }
            if (__state.mod) { __instance.size = __state.size; Pending.Remove(__instance.Id()); }
            if (__state.box >= 0)
            {
                var arr = __instance._hitResults;
                if (arr == null) return;
                for (int i = 0; i < arr.Length; i++)
                {
                    var h = arr[i];
#if IL2CPP
                    if (h.m_Collider == 0) continue;
                    var point = h.m_Point;
#else
                    if (h.colliderInstanceID == 0) continue;
                    var point = h.point;
#endif
                    bool character = ClassifyHit(h);
                    Overlay.MarkHit(__state.box, point, character);
                }
            }
        }
        catch (Exception e) { Plugin.L.LogError("Postfix: " + e.Message); }
    }

    // Слой коллайдера: персонажи (хитбоксы NPC/героя) — «цель», остальное — окружение. Пишем в лог для настройки.
    private static bool ClassifyHit(RaycastHit h)
    {
        try
        {
            var col = h.collider;
            if (col == null) return false;
            var go = col.gameObject;
            string layer = LayerMask.LayerToName(go.layer) ?? go.layer.ToString();
            string l = layer.ToLowerInvariant();
            bool character = l.Contains("hitbox") || l.Contains("npc") || l.Contains("character") || l.Contains("ai") || l.Contains("hero") || l.Contains("ragdoll");
            if (Plugin.LogHits.Value && LoggedHits.Count < 80)
            {
                var root = go.transform.root != null ? go.transform.root.name : "";
                string key = layer + "|" + go.name;
                if (LoggedHits.Add(key)) Plugin.L.LogInfo($"[hit] layer='{layer}'({go.layer}) obj='{go.name}' root='{root}' -> {(character ? "character" : "environment")}");
            }
            return character;
        }
        catch { return false; }
    }

    // Новая цель получила урон от героя за этот взмах (игра зовёт один раз на цель).
    [HarmonyPostfix, HarmonyPatch(typeof(Awaken.TG.Main.Heroes.Combat.CharacterDealingDamage), nameof(Awaken.TG.Main.Heroes.Combat.CharacterDealingDamage.AddDamageDealtTo))]
    private static void DamageDealtTo(Awaken.TG.Main.Heroes.Combat.CharacterDealingDamage __instance)
    {
        try
        {
            var hero = Awaken.TG.Main.Heroes.Hero.Current;
            var cd = hero?.CharacterDealingDamage;
            if (cd != null && cd.Id() == __instance.Id()) TargetsThisSwing++;
        }
        catch { }
    }

    // Начало удара героя — старт окна доводки/прилипания, сброс счётчика целей.
    [HarmonyPostfix, HarmonyPatch(typeof(CharacterWeapon), nameof(CharacterWeapon.AttackBegun))]
    private static void AttackBegunPostfix(CharacterWeapon __instance)
    {
        try { if (__instance._attachedToHero) { TargetsThisSwing = 0; _inAliveHit = false; if (Plugin.On && IsMeleeWeapon(__instance)) Assist.Trigger("attack"); } }
        catch (Exception e) { Plugin.L.LogError("AttackBegun: " + e.Message); }
    }

    // ---- projectiles ----
    [HarmonyPrefix, HarmonyPatch(typeof(Awaken.TG.Main.AI.Fights.Projectiles.DamageDealingProjectile), nameof(Awaken.TG.Main.AI.Fights.Projectiles.DamageDealingProjectile.ThrowCast))]
    private static void ThrowCastPre(Awaken.TG.Main.AI.Fights.Projectiles.DamageDealingProjectile __instance, float deltaTime)
    {
        try { Projectiles.Before(__instance, deltaTime); }
        catch (Exception e) { Projectiles.Fail(e); }
    }

    // ---- hit feedback (hitmarkers, floating labels) ----
    // HealthElement.TakeDamage is a tiny wrapper that IL2CPP inlines into callers (a patch there never runs);
    // TakeDamageInternal is the virtual worker (NPCs use the base one; the hero overrides it).
    [HarmonyPostfix, HarmonyPatch(typeof(Awaken.TG.Main.Character.HealthElement), nameof(Awaken.TG.Main.Character.HealthElement.TakeDamageInternal))]
    private static void TakeDamagePost(Awaken.TG.Main.Fights.DamageInfo.Damage damage)
    {
        try { HitFeedback.OnDamage(damage); } catch { }
    }

    // The hero's crit roll happens in ApplyDamageModifiers → CheckCriticalProbability and is not stored in Damage
    // (Damage.Critical is only the "forced crit" flag), so remember the roll per damage object.
    [HarmonyPostfix, HarmonyPatch(typeof(Awaken.TG.Main.Character.HealthElement), nameof(Awaken.TG.Main.Character.HealthElement.CheckCriticalProbability))]
    private static void CritRoll(Awaken.TG.Main.Fights.DamageInfo.Damage damage, bool __result)
    {
        try { if (__result && damage != null) HitFeedback.MarkCrit(damage.Id()); } catch { }
    }

    // ---- hit-stop ----
    // Both MeleeAttackAnimatorState.OnAliveHit (weapon hit a character) and OnEnvironmentHit (wall/prop)
    // call MeleeFSM.HitStop, which adds the MeleeHitStop element: animation slow-down, the "bounce"
    // and blend back to idle. Skipping that call keeps the swing animation playing unobstructed.
    private static bool _inAliveHit;
    private static int _hitStopLogged;

    [HarmonyPrefix, HarmonyPatch(typeof(MeleeAttackAnimatorState), nameof(MeleeAttackAnimatorState.OnAliveHit))]
    private static void AliveHitPre() => _inAliveHit = true;

    [HarmonyPostfix, HarmonyPatch(typeof(MeleeAttackAnimatorState), nameof(MeleeAttackAnimatorState.OnAliveHit))]
    private static void AliveHitPost() => _inAliveHit = false;

    [HarmonyPrefix, HarmonyPatch(typeof(MeleeFSM), nameof(MeleeFSM.HitStop))]
    private static bool HitStopPre()
    {
        try
        {
            if (!Plugin.On) return true;
            bool enemy = _inAliveHit;
            bool skip = enemy ? Plugin.NoHitStopEnemies.Value : Plugin.NoHitStopWalls.Value;
            if (skip && Plugin.LogHits.Value && _hitStopLogged++ < 20) Plugin.L.LogInfo($"[hitstop] skipped ({(enemy ? "enemy" : "environment")})");
            return !skip;
        }
        catch { return true; }
    }

    // Aim assist and magnet are for melee only — bows and staffs/wands are left alone.
    private static bool IsMeleeWeapon(CharacterWeapon w)
    {
        try
        {
            var tpl = w.Item?.Template;
            if (tpl == null) return true;
            return !(tpl.IsRanged || tpl.IsCastMagic || tpl.IsRod || tpl.IsThrowable);
        }
        catch { return true; }
    }

    internal static string ShortName(string n)
    {
        n = n.Replace("(Clone)", "");
        var i = n.LastIndexOf('_');
        return i > 0 && i < n.Length - 1 ? n.Substring(i + 1) : n;
    }
}
