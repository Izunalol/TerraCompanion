using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TerraCompanion.Core.Game;

/// <summary>Colors the in-game minimap uses, recovered from Terraria.Map.MapHelper.Initialize.</summary>
public sealed class MapColors
{
    public uint[] Tile = Array.Empty<uint>();   // ARGB, 0 = unknown
    public uint[] Wall = Array.Empty<uint>();
    public uint[] Liquid = { 0xFF093DBF, 0xFFFD2003, 0xFFFEC214, 0xFFA17FFF }; // water, lava, honey, shimmer
    public uint SkyTop = 0xFF3228FF, SkyBottom = 0xFF91B9FF;
    public uint DirtTop = 0xFF583D2E, DirtBottom = 0xFF254E59; // replaced by the extractor when found
    public uint RockTop = 0xFF4A433C, RockBottom = 0xFF4A433C;
    public uint Hell = 0xFF320A0A;
    public int TilesFound, WallsFound;
}

/// <summary>
/// A tiny symbolic IL interpreter. MapHelper.Initialize is ~34KB of straight-line
/// "colors[tile][option] = new Color(r, g, b)" code; running it symbolically (linearly, ignoring
/// branches) recovers the real palette without starting the game or loading XNA.
/// </summary>
public static class MapColorExtractor
{
    abstract record V;
    sealed record IntV(long N) : V;
    sealed record FltV(double N) : V;
    sealed record ColV(uint Argb) : V;
    sealed record ArrV(int Id, string Tag) : V;
    sealed record RowV(int Arr, long Row) : V;
    sealed record AddrV(int Local) : V;
    sealed record SymV(string Name) : V;
    sealed record UnkV : V { public static readonly UnkV I = new(); }

    public static MapColors Extract(ModuleDefinition module, int tileCount, int wallCount)
    {
        var result = new MapColors { Tile = new uint[tileCount], Wall = new uint[wallCount] };
        var type = module.GetType("Terraria.Map.MapHelper");
        var method = type?.Methods.FirstOrDefault(m => m.Name == "Initialize" && m.HasBody);
        if (method == null) return result;

        var stack = new Stack<V>();
        var locals = new Dictionary<int, V>();
        var arrays = new List<string>();
        var store = new Dictionary<(int arr, long row, long col), uint>();
        var ctorLocals = new List<uint>(); // colors constructed in-place into locals, in order

        V Pop() => stack.Count > 0 ? stack.Pop() : UnkV.I;
        void Push(V v) => stack.Push(v);

        foreach (var ins in method.Body.Instructions)
        {
            var op = ins.OpCode;
            try
            {
                switch (op.Code)
                {
                    case Code.Ldc_I4_M1: Push(new IntV(-1)); continue;
                    case Code.Ldc_I4_0: case Code.Ldc_I4_1: case Code.Ldc_I4_2: case Code.Ldc_I4_3: case Code.Ldc_I4_4:
                    case Code.Ldc_I4_5: case Code.Ldc_I4_6: case Code.Ldc_I4_7: case Code.Ldc_I4_8:
                        Push(new IntV(op.Code - Code.Ldc_I4_0)); continue;
                    case Code.Ldc_I4: Push(new IntV((int)ins.Operand)); continue;
                    case Code.Ldc_I4_S: Push(new IntV((sbyte)ins.Operand)); continue;
                    case Code.Ldc_R4: Push(new FltV((float)ins.Operand)); continue;
                    case Code.Ldc_R8: Push(new FltV((double)ins.Operand)); continue;
                    case Code.Ldsfld:
                    {
                        var f = (FieldReference)ins.Operand;
                        Push(f.Name == "Count" ? new SymV(f.DeclaringType.Name + ".Count") : UnkV.I);
                        continue;
                    }
                    case Code.Newarr:
                    {
                        var size = Pop();
                        var elem = (TypeReference)ins.Operand;
                        string tag = size is SymV s ? s.Name : size is IntV n ? n.N.ToString() : "?";
                        tag = (elem.IsArray ? "2d:" : "1d:") + tag;
                        arrays.Add(tag);
                        Push(new ArrV(arrays.Count - 1, tag));
                        continue;
                    }
                    case Code.Ldloc_0: case Code.Ldloc_1: case Code.Ldloc_2: case Code.Ldloc_3:
                        Push(locals.GetValueOrDefault(op.Code - Code.Ldloc_0, UnkV.I)); continue;
                    case Code.Ldloc: case Code.Ldloc_S:
                        Push(locals.GetValueOrDefault(((VariableDefinition)ins.Operand).Index, UnkV.I)); continue;
                    case Code.Stloc_0: case Code.Stloc_1: case Code.Stloc_2: case Code.Stloc_3:
                        locals[op.Code - Code.Stloc_0] = Pop(); continue;
                    case Code.Stloc: case Code.Stloc_S:
                        locals[((VariableDefinition)ins.Operand).Index] = Pop(); continue;
                    case Code.Ldloca: case Code.Ldloca_S:
                        Push(new AddrV(((VariableDefinition)ins.Operand).Index)); continue;
                    case Code.Dup: { var v = Pop(); Push(v); Push(v); continue; }
                    case Code.Pop: Pop(); continue;
                    case Code.Ldelem_Ref:
                    {
                        var idx = Pop(); var arr = Pop();
                        Push(arr is ArrV a && idx is IntV i ? new RowV(a.Id, i.N) : UnkV.I);
                        continue;
                    }
                    case Code.Stelem_Any:
                    {
                        var val = Pop(); var idx = Pop(); var target = Pop();
                        if (val is ColV c && idx is IntV i)
                        {
                            if (target is RowV r) store[(r.Arr, r.Row, i.N)] = c.Argb;
                            else if (target is ArrV a) store[(a.Id, -1, i.N)] = c.Argb;
                        }
                        continue;
                    }
                    case Code.Ldelem_Any:
                    {
                        var idx = Pop(); var target = Pop();
                        uint col = 0;
                        bool ok = idx is IntV i && (target is RowV r ? store.TryGetValue((r.Arr, r.Row, i.N), out col)
                                                   : target is ArrV a && store.TryGetValue((a.Id, -1, i.N), out col));
                        Push(ok ? new ColV(col) : UnkV.I);
                        continue;
                    }
                    case Code.Add: case Code.Sub: case Code.Mul: case Code.Div:
                    {
                        var b = Pop(); var a = Pop();
                        Push(Arith(op.Code, a, b));
                        continue;
                    }
                    case Code.Conv_R4: case Code.Conv_R8: case Code.Conv_R_Un:
                    {
                        var a = Pop();
                        Push(a is IntV i ? new FltV(i.N) : a is FltV ? a : UnkV.I);
                        continue;
                    }
                    case Code.Conv_I4: case Code.Conv_I: case Code.Conv_U4: case Code.Conv_I8: case Code.Conv_U2: case Code.Conv_I2:
                    case Code.Conv_U1: case Code.Conv_I1:
                    {
                        var a = Pop();
                        long n = a is IntV i ? i.N : a is FltV f ? (long)f.N : long.MinValue;
                        if (n == long.MinValue) { Push(UnkV.I); continue; }
                        if (op.Code == Code.Conv_U1) n &= 0xFF;
                        if (op.Code == Code.Conv_U2) n &= 0xFFFF;
                        Push(new IntV(n));
                        continue;
                    }
                    case Code.Newobj:
                    {
                        var m = (MethodReference)ins.Operand;
                        var args = PopArgs(stack, m.Parameters.Count);
                        Push(m.DeclaringType.Name == "Color" ? MakeColor(args) : UnkV.I);
                        continue;
                    }
                    case Code.Call: case Code.Callvirt:
                    {
                        var m = (MethodReference)ins.Operand;
                        var args = PopArgs(stack, m.Parameters.Count);
                        V self = m.HasThis ? Pop() : UnkV.I;
                        V ret = UnkV.I;
                        if (m.DeclaringType.Name == "Color")
                        {
                            if (m.Name == ".ctor" && self is AddrV ad)
                            {
                                var c = MakeColor(args);
                                locals[ad.Local] = c;
                                if (c is ColV cv && arrays.Contains("1d:256")) ctorLocals.Add(cv.Argb);
                                continue;
                            }
                            var selfCol = self is AddrV a2 ? locals.GetValueOrDefault(a2.Local) as ColV : null;
                            ret = m.Name switch
                            {
                                "get_R" when selfCol != null => new IntV((selfCol.Argb >> 16) & 0xFF),
                                "get_G" when selfCol != null => new IntV((selfCol.Argb >> 8) & 0xFF),
                                "get_B" when selfCol != null => new IntV(selfCol.Argb & 0xFF),
                                "get_A" when selfCol != null => new IntV(selfCol.Argb >> 24),
                                "op_Multiply" when args[0] is ColV c0 && args[1] is FltV f => new ColV(Multiply(c0.Argb, f.N, true)),
                                "Lerp" when args[0] is ColV ca && args[1] is ColV cb && args[2] is FltV t => new ColV(Lerp(ca.Argb, cb.Argb, t.N)),
                                _ => UnkV.I
                            };
                        }
                        else if (m.Name == "MultiplyMapColor" && args.Length == 2 && args[0] is ColV mc && args[1] is FltV mf)
                            ret = new ColV(Multiply(mc.Argb, mf.N, false));
                        if (m.ReturnType.MetadataType != MetadataType.Void) Push(ret);
                        continue;
                    }
                }
                // Anything else: keep the stack balanced using the opcode's declared behaviour.
                for (int i = PopCount(op); i > 0; i--) Pop();
                for (int i = PushCount(op); i > 0; i--) Push(UnkV.I);
            }
            catch
            {
                stack.Clear();
            }
        }

        // Identify arrays by how they were sized: Color[TileID.Count][], Color[WallID.Count][].
        int tileArr = arrays.FindIndex(t => t == "2d:TileID.Count");
        int wallArr = arrays.FindIndex(t => t == "2d:WallID.Count");
        int liquidArr = arrays.FindIndex(t => t == "1d:4");
        foreach (var ((arr, row, col), c) in store)
        {
            if (col != 0 || row < 0) continue;
            if (arr == tileArr && row < tileCount) result.Tile[row] = c;
            else if (arr == wallArr && row < wallCount) result.Wall[row] = c;
        }
        // Fall back to the first non-zero option for entries whose option 0 is missing.
        foreach (var ((arr, row, col), c) in store.OrderBy(k => k.Key.col))
        {
            if (row < 0) continue;
            if (arr == tileArr && row < tileCount && result.Tile[row] == 0) result.Tile[row] = c;
            else if (arr == wallArr && row < wallCount && result.Wall[row] == 0) result.Wall[row] = c;
        }
        if (liquidArr >= 0)
            for (int i = 0; i < 4; i++)
                if (store.TryGetValue((liquidArr, -1, i), out var lc)) result.Liquid[i] = lc;
        // Gradient endpoints are built in-place into locals in order: sky, dirt, rock pairs, then hell.
        if (ctorLocals.Count >= 7)
        {
            result.SkyTop = ctorLocals[0]; result.SkyBottom = ctorLocals[1];
            result.DirtTop = ctorLocals[2]; result.DirtBottom = ctorLocals[3];
            result.RockTop = ctorLocals[4]; result.RockBottom = ctorLocals[5];
            result.Hell = ctorLocals[6];
        }
        result.TilesFound = result.Tile.Count(c => c != 0);
        result.WallsFound = result.Wall.Count(c => c != 0);
        return result;
    }

    static V[] PopArgs(Stack<V> s, int n)
    {
        var a = new V[n];
        for (int i = n - 1; i >= 0; i--) a[i] = s.Count > 0 ? s.Pop() : UnkV.I;
        return a;
    }

    static V Arith(Code c, V a, V b)
    {
        if (a is IntV x && b is IntV y)
            return c switch { Code.Add => new IntV(x.N + y.N), Code.Sub => new IntV(x.N - y.N), Code.Mul => new IntV(x.N * y.N), _ => y.N == 0 ? UnkV.I : new IntV(x.N / y.N) };
        double? p = a is IntV ai ? ai.N : a is FltV af ? af.N : null;
        double? q = b is IntV bi ? bi.N : b is FltV bf ? bf.N : null;
        if (p == null || q == null) return UnkV.I;
        return c switch { Code.Add => new FltV(p.Value + q.Value), Code.Sub => new FltV(p.Value - q.Value), Code.Mul => new FltV(p.Value * q.Value), _ => new FltV(p.Value / q.Value) };
    }

    static V MakeColor(V[] a)
    {
        static int B(double v) => (int)Math.Clamp(v, 0, 255);
        if (a.Length is 3 or 4 && a.All(v => v is IntV))
        {
            var n = a.Select(v => (int)((IntV)v).N).ToArray();
            return new ColV(Pack(B(n[0]), B(n[1]), B(n[2]), a.Length == 4 ? B(n[3]) : 255));
        }
        if (a.Length is 3 or 4 && a.All(v => v is FltV))
        {
            var n = a.Select(v => ((FltV)v).N * 255).ToArray();
            return new ColV(Pack(B(n[0]), B(n[1]), B(n[2]), a.Length == 4 ? B(n[3]) : 255));
        }
        return UnkV.I;
    }

    static uint Pack(int r, int g, int b, int a) => (uint)(a << 24 | r << 16 | g << 8 | b);

    static uint Multiply(uint c, double f, bool alphaToo)
    {
        int Ch(int sh) => (int)Math.Clamp(((c >> sh) & 0xFF) * f, 0, 255);
        return Pack(Ch(16), Ch(8), Ch(0), alphaToo ? Ch(24) : (int)(c >> 24));
    }

    static uint Lerp(uint a, uint b, double t)
    {
        int Ch(int sh) => (int)Math.Clamp(((a >> sh) & 0xFF) + (((int)((b >> sh) & 0xFF) - (int)((a >> sh) & 0xFF)) * t), 0, 255);
        return Pack(Ch(16), Ch(8), Ch(0), Ch(24));
    }

    static int PopCount(OpCode op) => op.StackBehaviourPop switch
    {
        StackBehaviour.Pop0 => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8
            or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4
            or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
        _ => 0
    };

    static int PushCount(OpCode op) => op.StackBehaviourPush switch
    {
        StackBehaviour.Push0 => 0,
        StackBehaviour.Push1_push1 => 2,
        _ => 1
    };
}
