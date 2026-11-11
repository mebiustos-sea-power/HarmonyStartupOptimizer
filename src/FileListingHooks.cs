using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace HarmonyStartupOptimizer;

internal sealed class FileListingHooks : IOptimizerModule
{
    [ThreadStatic] private static Frame? current;
    private static FileListingHooks? installed;
    private readonly PatchRegistry patches;
    private readonly Func<MethodInfo> findTarget;
    public string Name => "FileListing";

    internal FileListingHooks(string owner, Assembly game) : this(owner, () => FindTarget(game)) { }
    internal FileListingHooks(string owner, MethodInfo target) : this(owner, () => target) { }
    private FileListingHooks(string owner, Func<MethodInfo> findTarget)
    { patches = new PatchRegistry(new Harmony(owner)); this.findTarget = findTarget; }

    private static MethodInfo FindTarget(Assembly game)
    {
        Type fileManager = game.GetType("SeaPower.FileManager", true)!;
        Type? layerKind = game.GetType("SeaPower.LayerKind");
        Type[] parameters = layerKind == null
            ? new[] { typeof(string), typeof(string) }
            : new[] { typeof(string), typeof(string), layerKind };
        return fileManager.GetMethod("GetFiles", parameters) ?? throw new MissingMethodException(fileManager.FullName,
            layerKind == null ? "GetFiles(string,string)" : "GetFiles(string,string,LayerKind)");
    }

    public void Install()
    {
        if (installed != null) throw new InvalidOperationException("File-list optimization is already installed.");
        MethodInfo target = findTarget();
        if (target.ReturnType != typeof(List<string>)) throw new InvalidOperationException("Unsupported file-list return type: " + target);
        installed = this;
        patches.Add(target, prefix: Callback(nameof(Prefix)), finalizer: Callback(nameof(Finalizer)), transpiler: Callback(nameof(Transpiler)));
    }

    public void Uninstall()
    {
        if (installed == this) installed = null;
        patches.RemoveAll();
    }

    private static HarmonyMethod Callback(string name) => new HarmonyMethod(typeof(FileListingHooks), name) { priority = Priority.First };
    private static void Prefix(out Frame __state) { __state = new Frame(current); current = __state; }
    private static void Finalizer(Frame? __state) { if (__state != null) current = __state.Parent; }

    private static bool TryContains(List<string> source, string value, out bool result)
    {
        result = false;
        if (installed == null || current == null) return false;
        result = current.For(source).Contains(value);
        return true;
    }

    private static void Add(List<string> source, string value)
    {
        source.Add(value);
        if (installed != null && current != null) current.For(source).Add(value);
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        int contains = 0, adds = 0;
        LocalBuilder list = generator.DeclareLocal(typeof(List<string>));
        LocalBuilder value = generator.DeclareLocal(typeof(string));
        LocalBuilder result = generator.DeclareLocal(typeof(bool));
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Callvirt && instruction.operand is MethodInfo method && method.DeclaringType == typeof(List<string>))
            {
                if (method.Name == nameof(List<string>.Contains))
                {
                    Label original = generator.DefineLabel(), done = generator.DefineLabel();
                    var save = new CodeInstruction(OpCodes.Stloc, value);
                    instruction.MoveLabelsTo(save);
                    foreach (ExceptionBlock block in instruction.blocks)
                        if (block.blockType != ExceptionBlockType.EndExceptionBlock) save.blocks.Add(block);
                    instruction.blocks.RemoveAll(b => b.blockType != ExceptionBlockType.EndExceptionBlock);
                    yield return save;
                    yield return new CodeInstruction(OpCodes.Stloc, list);
                    yield return new CodeInstruction(OpCodes.Ldloc, list);
                    yield return new CodeInstruction(OpCodes.Ldloc, value);
                    yield return new CodeInstruction(OpCodes.Ldloca, result);
                    yield return new CodeInstruction(OpCodes.Call, typeof(FileListingHooks).GetMethod(nameof(TryContains), BindingFlags.Static | BindingFlags.NonPublic)!);
                    yield return new CodeInstruction(OpCodes.Brfalse, original);
                    yield return new CodeInstruction(OpCodes.Ldloc, result);
                    yield return new CodeInstruction(OpCodes.Br, done);
                    var load = new CodeInstruction(OpCodes.Ldloc, list);
                    ((IList)typeof(CodeInstruction).GetField("labels")!.GetValue(load)!).Add(original);
                    yield return load;
                    yield return new CodeInstruction(OpCodes.Ldloc, value);
                    // Keep the original call available to other transpilers and
                    // to an invocation already running while this module stops.
                    yield return instruction;
                    var end = new CodeInstruction(OpCodes.Nop);
                    ((IList)typeof(CodeInstruction).GetField("labels")!.GetValue(end)!).Add(done);
                    yield return end;
                    contains++;
                    continue;
                }
                if (method.Name == nameof(List<string>.Add))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = typeof(FileListingHooks).GetMethod(nameof(Add), BindingFlags.Static | BindingFlags.NonPublic)!;
                    adds++;
                }
            }
            yield return instruction;
        }
        if (contains != 1 || adds != 1) throw new InvalidOperationException("Unsupported file-list layout: expected one duplicate check and append.");
    }

    private sealed class Frame
    {
        internal readonly Frame? Parent;
        private List<string>? source;
        private HashSet<string>? seen;
        internal Frame(Frame? parent) { Parent = parent; }
        internal HashSet<string> For(List<string> list)
        {
            if (!ReferenceEquals(source, list))
            { source = list; seen = new HashSet<string>(list, StringComparer.Ordinal); }
            return seen!;
        }
    }
}
