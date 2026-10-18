#if DEBUG && IL2CPP
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Iced.Intel;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;

namespace CombatQOL;

// Инструмент разработки: дизассемблирует нативный код interop-методов и подписывает поля/вызовы.
// Включается [Debug] Diagnostics = true. Вывод — BepInEx\CombatQOL_diag.txt.
//   DiagMethods:  "Полное.Имя.Типа:Метод1,Метод2; ДругойТип:Метод"
//   DiagFieldScan: "Полное.Имя.Типа:поле1,поле2" — какие методы типа трогают эти поля
internal static unsafe class Diag
{
    private sealed class PtrReader : CodeReader
    {
        private readonly byte* _p; private int _i;
        public PtrReader(IntPtr p) { _p = (byte*)p; }
        public override int ReadByte() => _p[_i++];
    }

    private static StreamWriter _w;
    private static readonly Dictionary<long, string> Targets = new();

    private static Type FindType(string fullName)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = a.GetType(fullName, false);
            if (t != null) return t;
        }
        return null;
    }

    public static Dictionary<int, string> FieldOffsets(Type t)
    {
        var map = new Dictionary<int, string>();
        for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var f in cur.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (!f.Name.StartsWith("NativeFieldInfoPtr_") || f.FieldType != typeof(IntPtr)) continue;
                if (cur.ContainsGenericParameters) continue;
                IntPtr fi;
                try { fi = (IntPtr)f.GetValue(null); } catch { continue; }
                if (fi == IntPtr.Zero) continue;
                if ((IL2CPP.il2cpp_field_get_flags(fi) & 0x10) != 0) continue;
                var off = (int)IL2CPP.il2cpp_field_get_offset(fi);
                map.TryAdd(off, cur.Name + "." + f.Name.Substring("NativeFieldInfoPtr_".Length));
            }
        }
        return map;
    }

    public static IntPtr NativeCode(MethodBase m)
    {
        if (m.ContainsGenericParameters || (m.DeclaringType?.ContainsGenericParameters ?? false)) return IntPtr.Zero;
        var f = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(m);
        if (f == null) return IntPtr.Zero;
        var mi = (IntPtr)f.GetValue(null);
        return mi == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)mi;
    }

    private static void AddTargets(Type t)
    {
        if (t == null) return;
        foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            try { var c = NativeCode(m); if (c != IntPtr.Zero) Targets[c.ToInt64()] = t.Name + "." + m.Name; } catch { }
        }
    }

    private static IEnumerable<Instruction> Decode(IntPtr code, int maxBytes = 8000)
    {
        var dec = Iced.Intel.Decoder.Create(64, new PtrReader(code), (ulong)code.ToInt64());
        ulong end = (ulong)code.ToInt64() + (ulong)maxBytes;
        int rets = 0;
        while (dec.IP < end)
        {
            var ins = dec.Decode();
            if (ins.IsInvalid) yield break;
            yield return ins;
            if (ins.Mnemonic == Mnemonic.Int3) yield break;
            if (ins.Mnemonic == Mnemonic.Ret && ++rets > 12) yield break;
        }
    }

    private static string Note(in Instruction ins, Dictionary<int, string> offs)
    {
        if (ins.FlowControl == FlowControl.Call && ins.Op0Kind == OpKind.NearBranch64 && Targets.TryGetValue((long)ins.NearBranch64, out var tn))
            return "-> " + tn;
        for (int op = 0; op < ins.OpCount; op++)
        {
            if (ins.GetOpKind(op) != OpKind.Memory) continue;
            if (ins.MemoryBase == Register.RSP || ins.MemoryBase == Register.RBP || ins.IsIPRelativeMemoryOperand) continue;
            if (offs.TryGetValue((int)ins.MemoryDisplacement64, out var n)) return n;
        }
        return "";
    }

    private static void DumpMethod(MethodInfo m, Dictionary<int, string> offs)
    {
        var code = NativeCode(m);
        _w.WriteLine($"=== {m.DeclaringType?.Name}.{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}) @0x{code.ToInt64():X}");
        if (code == IntPtr.Zero) return;
        var fmt = new IntelFormatter(); var sb = new StringOutput();
        foreach (var ins in Decode(code))
        {
            fmt.Format(ins, sb);
            _w.WriteLine($"  +{ins.IP - (ulong)code.ToInt64():X4}  {sb.ToStringAndReset().PadRight(58)} {Note(ins, offs)}");
        }
    }

    public static void Run(string methods, string fieldScan)
    {
        try
        {
            var path = Path.Combine(BepInEx.Paths.BepInExRootPath, "CombatQOL_diag.txt");
            _w = new StreamWriter(path, false);
            foreach (var n in new[] { "UnityEngine.Transform", "UnityEngine.Component", "UnityEngine.Vector3", "UnityEngine.Quaternion", "UnityEngine.Mathf",
                         "UnityEngine.Camera", "UnityEngine.Physics", "UnityEngine.CharacterController", "UnityEngine.Time",
                         "Awaken.TG.Main.Heroes.Combat.HeroCamera", "Awaken.TG.Main.Heroes.Combat.VHeroController", "Awaken.TG.Main.Heroes.Hero" })
                AddTargets(FindType(n));

            foreach (var part in (methods ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':', 2);
                var t = FindType(kv[0].Trim());
                if (t == null) { _w.WriteLine("type not found: " + kv[0]); continue; }
                AddTargets(t);
                var offs = FieldOffsets(t);
                _w.WriteLine($"##### {t.FullName}");
                foreach (var kvp in offs.OrderBy(k => k.Key)) _w.WriteLine($"field 0x{kvp.Key:X} {kvp.Value}");
                var names = kv.Length > 1 ? kv[1].Split(',').Select(s => s.Trim()).ToHashSet() : new HashSet<string>();
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (names.Contains(m.Name)) { try { DumpMethod(m, offs); } catch (Exception e) { _w.WriteLine($"=== {m.Name}: {e.Message}"); } }
            }

            foreach (var part in (fieldScan ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':', 2);
                var t = FindType(kv[0].Trim());
                if (t == null || kv.Length < 2) continue;
                var offs = FieldOffsets(t);
                var wanted = kv[1].Split(',').Select(s => s.Trim()).ToHashSet();
                var want = offs.Where(o => wanted.Contains(o.Value.Substring(o.Value.IndexOf('.') + 1))).ToDictionary(o => o.Key, o => o.Value);
                _w.WriteLine($"##### field scan {t.FullName}: {string.Join(", ", want.Select(w => $"{w.Value}@0x{w.Key:X}"))}");
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    IntPtr code;
                    try { code = NativeCode(m); } catch { continue; }
                    if (code == IntPtr.Zero) continue;
                    var fmt = new IntelFormatter(); var sb = new StringOutput();
                    foreach (var ins in Decode(code))
                    {
                        var note = Note(ins, want);
                        if (note.Length == 0 || note.StartsWith("->")) continue;
                        fmt.Format(ins, sb);
                        _w.WriteLine($"  {m.Name}: +{ins.IP - (ulong)code.ToInt64():X4} {sb.ToStringAndReset()}  {note}");
                    }
                }
            }
            _w.Flush(); _w.Close();
            Plugin.L.LogInfo("Diag written: " + path);
        }
        catch (Exception e) { Plugin.L.LogError("Diag: " + e); try { _w?.Close(); } catch { } }
    }
}

#endif
