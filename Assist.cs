using System;
using Awaken.TG.Main.Fights.Factions;
using Awaken.TG.Main.Fights.NPCs;
using Awaken.TG.Main.Heroes;
using UnityEngine;
using TGShared;

namespace CombatQOL;

// Доводка прицела и прилипание героя к врагу в окне ближней атаки.
// Цель выбирается один раз в начале удара (враждебный, живой, в конусе MaxAngle и в пределах Range),
// дальше каждый кадр окна камера доворачивается не быстрее Speed °/с, герой подтягивается не дальше MaxPerAttack.
internal static class Assist
{
    private static NpcElement _target;
    private static float _until;
    private static float _moved;
    internal static string Status = "";
    private static int _errors;
    internal static bool Disabled;

    // Допустимый угол от прицела до цели на дистанции d: враг не дальше CaptureWidth метров вбок.
    internal static float AllowedAngle(float d) => Mathf.Min(60f, Mathf.Atan2(Plugin.CaptureWidth.Value, Mathf.Max(0.3f, d)) * Mathf.Rad2Deg);

    private static Camera Cam()
    {
        var c = Camera.main;
        if (c != null) return c;
        var all = Camera.allCameras;
        return all != null && all.Length > 0 ? all[0] : null;
    }

    private static bool GameplayActive()
    {
        return Time.timeScale > 0.01f && Cursor.lockState == CursorLockMode.Locked;
    }

    internal static Vector3 AimPoint(NpcElement npc)
    {
        try
        {
            var t = npc.Torso;
            if (t != null) return t.position;
        }
        catch { }
        return npc.Coords + Vector3.up * Mathf.Max(0.8f, npc.Height * 0.6f);
    }

    internal static void Trigger(string why)
    {
        if (Disabled || !(Plugin.AimEnabled.Value || Plugin.MagnetEnabled.Value) || !GameplayActive()) return;
        try
        {
            var hero = Hero.Current;
            var cam = Cam();
            if (hero == null || cam == null) return;
            var best = FindTarget(hero, cam, out var dist, out var angle, out var scanned);
            bool fresh = Time.time > _until;
            if (best == null)
            {
                if (fresh) _target = null;
                Status = Loc.T("st_notarget");
                return;
            }
            if (fresh || _target == null || _target.Id() != best.Id()) _moved = 0f;
            _target = best;
            _until = Time.time + Plugin.AssistWindow.Value;
            Status = Loc.F("st_target", SafeName(best), angle, AllowedAngle(dist), 0f);
            if (Plugin.LogAssist.Value) Plugin.L.LogInfo($"[assist:{why}] target {SafeName(best)} dist={dist:0.00} angle={angle:0.0} scanned={scanned}");
        }
        catch (Exception e) { Fail("Trigger", e); }
    }

    private static string SafeName(NpcElement n)
    {
        try { return n.Name; } catch { return "?"; }
    }

    private static NpcElement FindTarget(Hero hero, Camera cam, out float bestDist, out float bestAngle, out int scanned)
    {
        bestDist = 0; bestAngle = 0; scanned = 0;
        var chunk = hero.NpcChunk;
        var grid = chunk?._grid;
        var chunks = grid?.Chunks;
        if (chunks == null) return null;

        var camT = cam.transform;
        Vector3 camPos = camT.position, camFwd = camT.forward, heroPos = hero.Coords;
        float range = Plugin.TargetRange.Value;
        NpcElement best = null;
        float bestScore = float.MaxValue;

        for (int c = 0; c < chunks.Length; c++)
        {
            var ch = chunks[c];
            if (ch == null) continue;
            var list = ch.Npcs;
            if (list == null) continue;
            for (int i = 0; i < list.Count; i++)
            {
                var npc = list[i];
                if (npc == null) continue;
                Vector3 pos;
                try { pos = npc.Coords; } catch { continue; }
                var flat = pos - heroPos; flat.y = 0;
                float r = SafeRadius(npc);
                float d = flat.magnitude - r;
                if (d > range || Mathf.Abs(pos.y - heroPos.y) > 3f) continue;
                scanned++;
                try
                {
                    if (!npc.IsAlive || npc.IsDying) continue;
                    if (!WithFactionUtils.IsHostileTo(hero, npc)) continue;
                }
                catch { continue; }
                var aim = AimPoint(npc);
                var dir = aim - camPos;
                float ang = Vector3.Angle(camFwd, dir);
                float allowed = AllowedAngle(dir.magnitude);
                if (ang > allowed) continue;
                if (Plugin.RequireLineOfSight.Value && !Visible(camPos, aim, npc)) continue;
                float score = ang / allowed + 0.6f * Mathf.Max(0, d) / range;
                if (score < bestScore) { bestScore = score; best = npc; bestDist = Mathf.Max(0, d); bestAngle = ang; }
            }
        }
        return best;
    }

    private static float SafeRadius(NpcElement npc)
    {
        try { return Mathf.Clamp(npc.Radius, 0.2f, 3f); } catch { return 0.4f; }
    }

    // Луч до цели: видна, если первое препятствие не ближе, чем почти до самой цели.
    private static bool Visible(Vector3 from, Vector3 to, NpcElement npc)
    {
        var dir = to - from;
        float len = dir.magnitude;
        if (len < 0.01f) return true;
        if (!Physics.Raycast(from, dir / len, out var hit, len, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return true;
        return hit.distance >= len - SafeRadius(npc) - 0.6f;
    }

    // Каждый кадр из Overlay.Update.
    internal static void Tick()
    {
        if (_target == null) return;
        if (!Plugin.On) { _target = null; return; }
        if (Time.time > _until || !GameplayActive()) { _target = null; return; }
        try
        {
            var hero = Hero.Current;
            var npc = _target;
            if (hero == null || !npc.IsAlive || npc.IsDying) { _target = null; return; }
            var vhc = hero.VHeroController;
            var cam = Cam();
            if (vhc == null || cam == null) return;
            float dt = Time.deltaTime;

            var camT = cam.transform;
            var aim = AimPoint(npc);
            var dir = aim - camT.position;
            float angle = Vector3.Angle(camT.forward, dir);
            float allowed = AllowedAngle(dir.magnitude);

            // Игрок сам отвернулся далеко — отпускаем, без «магнита через полэкрана».
            if (angle > allowed * 1.6f) { _target = null; return; }

            if (Plugin.AimEnabled.Value && angle > 0.5f)
            {
                var hc = vhc.HeroCamera;
                if (hc != null)
                {
                    var ang = hc.GetAngles();
                    float wantYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                    float wantPitch = -Mathf.Asin(Mathf.Clamp(dir.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
                    float dy = Mathf.DeltaAngle(ang.y, wantYaw);
                    float dp = Plugin.AimPitch.Value ? Mathf.DeltaAngle(ang.x, wantPitch) : 0f;
                    float max = Plugin.AimSpeed.Value * dt;
                    // мягкий старт и финиш: чем ближе к цели, тем медленнее
                    float k = Mathf.Clamp01(angle / 6f);
                    float stepY = Mathf.Clamp(dy, -max, max) * Mathf.Lerp(0.35f, 1f, k);
                    float stepP = Mathf.Clamp(dp, -max, max) * Mathf.Lerp(0.35f, 1f, k);
                    hc.SetAngles(new Vector3(ang.x + stepP, ang.y + stepY, ang.z));
                }
            }

            if (Plugin.MagnetEnabled.Value && _moved < Plugin.MagnetMaxPerAttack.Value)
            {
                var to = npc.Coords - hero.Coords; to.y = 0;
                float dist = to.magnitude;
                float stop = Plugin.MagnetStopGap.Value + SafeRadius(npc);
                if (dist > stop && dist - SafeRadius(npc) <= Plugin.TargetRange.Value && angle <= allowed)
                {
                    float step = Mathf.Min(Plugin.MagnetSpeed.Value * dt, dist - stop, Plugin.MagnetMaxPerAttack.Value - _moved);
                    if (step > 0.0005f)
                    {
                        vhc.PerformMoveStep(to / dist * step);
                        _moved += step;
                    }
                }
            }
            Status = Loc.F("st_target", SafeName(npc), angle, allowed, _moved);
        }
        catch (Exception e) { Fail("Tick", e); _target = null; }
    }

    // Мод не должен ронять игру и спамить: после 20 ошибок фича выключается.
    private static void Fail(string where, Exception e)
    {
        _errors++;
        if (_errors <= 5) Plugin.L.LogError($"Assist.{where}: {e.Message}");
        if (_errors == 20)
        {
            Plugin.L.LogError("Assist: too many errors, aim assist and magnet are off until restart");
            Disabled = true; _target = null;
        }
    }
}
