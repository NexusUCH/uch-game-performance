using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;

namespace UCHGamePerformance
{
    internal static class Guards
    {
        internal const string GameMvid = "8091acb1-d32d-4b39-87f0-c4fc4dfe5934";
        internal const string FirstpassMvid = "4f12d164-258d-4dae-bc16-866622fc79fb";
        internal static bool KnownBuild
        {
            get { return typeof(Placeable).Module.ModuleVersionId.ToString() == GameMvid && typeof(Smooth.State).Module.ModuleVersionId.ToString() == FirstpassMvid; }
        }

        internal static void Method(MethodBase method)
        {
            if (!KnownBuild || method == null) throw new InvalidOperationException("Unknown game build or missing method");
            string expected;
            string key = method.Module.Name + ":" + method.MetadataToken.ToString("X8");
            if (!GuardManifest.Hashes.TryGetValue(key, out expected)) throw new InvalidOperationException("Method is outside the audited manifest: " + method);
            MethodBody body = method.GetMethodBody();
            if (body == null) throw new InvalidOperationException("No IL body: " + method);
            string actual;
            using (SHA256 sha = SHA256.Create()) actual = BitConverter.ToString(sha.ComputeHash(body.GetILAsByteArray())).Replace("-", "");
            if (actual != expected || GuardManifest.Signatures[key] != SignaturePolicy.Read(method))
                throw new InvalidOperationException("Signature/IL fingerprint mismatch: " + method);
        }

        // Validate the entire incoming stream, including branch destinations, before editing anything.
        // This also rejects changed input supplied by an earlier transpiler. No Harmony owner scan.
        internal static List<CodeInstruction> Stream(IEnumerable<CodeInstruction> input, MethodBase original)
        {
            Method(original);
            List<CodeInstruction> code = new List<CodeInstruction>(input);
            List<CodeInstruction> vanilla = ReadOriginal(original);
            List<CodeInstruction> shape = code;
            if (code.Count != vanilla.Count)
            {
                // A preceding own transpiler may already have protected these leaves. Reverse
                // only our exact metadata-free nop + EndEH nop barriers for validation.
                shape = new List<CodeInstruction>();
                for (int i = 0; i < code.Count; i++)
                {
                    CodeInstruction copy = new CodeInstruction(code[i]); shape.Add(copy);
                    if ((copy.opcode == OpCodes.Leave || copy.opcode == OpCodes.Leave_S) && copy.blocks.Count == 0 && i + 2 < code.Count &&
                        code[i + 1].opcode == OpCodes.Nop && code[i + 1].labels.Count == 0 && code[i + 1].blocks.Count == 0 &&
                        code[i + 2].opcode == OpCodes.Nop && code[i + 2].labels.Count == 0 &&
                        code[i + 2].blocks.TrueForAll(b => b.blockType == ExceptionBlockType.EndExceptionBlock))
                    { copy.blocks.AddRange(code[i + 2].blocks); i += 2; }
                }
            }
            if (shape.Count != vanilla.Count) throw new InvalidOperationException("IL instruction count mismatch");
            for (int i = 0; i < shape.Count; i++)
                if (Operation(shape[i].opcode) != Operation(vanilla[i].opcode) || Operand(shape[i].operand, shape, shape[i].opcode.OperandType) != Operand(vanilla[i].operand, vanilla, vanilla[i].opcode.OperandType) || !SameBlocks(shape[i], vanilla[i]))
                    throw new InvalidOperationException("IL shape mismatch at " + i + " in " + original);
            // HarmonyX's ILManipulator.WriteTo suppresses a leave adjacent to EH metadata,
            // substituting its generated leave to the END of the handler. An explicit leave
            // can target farther ahead (SendEvent skips the dead-listener else clause).
            // Keep that explicit edge; the generated boundary leave becomes unreachable.
            for (int i = code.Count - 1; i >= 0; i--)
            {
                CodeInstruction instruction = code[i];
                if (instruction.opcode != OpCodes.Leave && instruction.opcode != OpCodes.Leave_S) continue;
                if (instruction.blocks.Count == 0 && (i + 1 == code.Count || code[i + 1].blocks.Count == 0)) continue;
                CodeInstruction boundary = new CodeInstruction(OpCodes.Nop);
                foreach (ExceptionBlock block in instruction.blocks)
                {
                    if (block.blockType != ExceptionBlockType.EndExceptionBlock)
                        throw new InvalidOperationException("Unsupported exception metadata on leave");
                    boundary.blocks.Add(block);
                }
                instruction.blocks.Clear();
                // First nop isolates the leave from boundary metadata; second owns EndEH.
                code.Insert(i + 1, new CodeInstruction(OpCodes.Nop));
                code.Insert(i + 2, boundary);
            }
            return code;
        }

        private static bool SameBlocks(CodeInstruction left, CodeInstruction right)
        {
            if (left.blocks.Count != right.blocks.Count) return false;
            for (int i = 0; i < left.blocks.Count; i++)
                if (left.blocks[i].blockType != right.blocks[i].blockType || left.blocks[i].catchType != right.blocks[i].catchType) return false;
            return true;
        }

        private static string Operation(OpCode opcode)
        {
            // Harmony widens short branches during method copying. Only the encoding width changes;
            // comparison still requires the same branch operation and exact destination instruction.
            return opcode.OperandType == OperandType.ShortInlineBrTarget ? opcode.Name.Substring(0, opcode.Name.Length - 2) : opcode.Name;
        }

        private static string Operand(object value, List<CodeInstruction> code, OperandType kind)
        {
            if (value == null) return "null";
            if (value is Label) return "label:" + LabelTarget((Label)value, code);
            Label[] labels = value as Label[];
            if (labels != null)
            {
                string result = "switch";
                for (int i = 0; i < labels.Length; i++) result += ":" + LabelTarget(labels[i], code);
                return result;
            }
            MemberInfo member = value as MemberInfo;
            if (member != null) return member.Module.ModuleVersionId + ":" + member.MetadataToken + ":" + member;
            LocalBuilder local = value as LocalBuilder;
            if (kind == OperandType.InlineVar || kind == OperandType.ShortInlineVar)
                return "variable:" + (local == null ? Convert.ToInt32(value) : local.LocalIndex);
            return value.GetType().FullName + ":" + Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static int LabelTarget(Label label, List<CodeInstruction> code)
        {
            for (int i = 0; i < code.Count; i++) if (code[i].labels.Contains(label)) return i;
            throw new InvalidOperationException("Unresolved IL branch label");
        }

        internal static bool Calls(CodeInstruction instruction, Type type, string name)
        {
            MethodInfo method = instruction.operand as MethodInfo;
            return (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && method != null && method.DeclaringType == type && method.Name == name;
        }

        internal static void Count(int actual, int expected, string name)
        {
            if (actual != expected) throw new InvalidOperationException(name + " expected " + expected + " site(s), found " + actual);
        }

        // A small standard ECMA-335 reader avoids asking Harmony to construct another method
        // while its transpilers are running. It also works in the offline .NET 8 harness.
        internal static List<CodeInstruction> ReadOriginal(MethodBase original)
        {
            byte[] bytes = original.GetMethodBody().GetILAsByteArray();
            Dictionary<short, OpCode> opcodes = new Dictionary<short, OpCode>();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (field.FieldType == typeof(OpCode)) { OpCode op = (OpCode)field.GetValue(null); opcodes[op.Value] = op; }
            List<CodeInstruction> code = new List<CodeInstruction>();
            Dictionary<int, int> offsets = new Dictionary<int, int>();
            List<int[]> branches = new List<int[]>();
            Type[] typeArgs = original.DeclaringType.GetGenericArguments();
            MethodInfo originalInfo = original as MethodInfo;
            Type[] methodArgs = originalInfo != null && originalInfo.IsGenericMethod ? originalInfo.GetGenericArguments() : Type.EmptyTypes;
            for (int position = 0; position < bytes.Length; )
            {
                offsets.Add(position, code.Count);
                short value = bytes[position++];
                if (value == 0xfe) value = (short)(0xfe00 | bytes[position++]);
                OpCode op = opcodes[value];
                object operand = null;
                int[] targets = null;
                switch (op.OperandType)
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineI: operand = (sbyte)bytes[position++]; break;
                    case OperandType.ShortInlineVar: operand = (int)bytes[position++]; break;
                    case OperandType.InlineVar: operand = (int)BitConverter.ToUInt16(bytes, position); position += 2; break;
                    case OperandType.InlineI: operand = BitConverter.ToInt32(bytes, position); position += 4; break;
                    case OperandType.InlineI8: operand = BitConverter.ToInt64(bytes, position); position += 8; break;
                    case OperandType.ShortInlineR: operand = BitConverter.ToSingle(bytes, position); position += 4; break;
                    case OperandType.InlineR: operand = BitConverter.ToDouble(bytes, position); position += 8; break;
                    case OperandType.InlineString: operand = original.Module.ResolveString(BitConverter.ToInt32(bytes, position)); position += 4; break;
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                        operand = original.Module.ResolveMember(BitConverter.ToInt32(bytes, position), typeArgs, methodArgs); position += 4; break;
                    case OperandType.ShortInlineBrTarget:
                        int shortDelta = (sbyte)bytes[position++]; targets = new int[] { position + shortDelta }; break;
                    case OperandType.InlineBrTarget:
                        int delta = BitConverter.ToInt32(bytes, position); position += 4; targets = new int[] { position + delta }; break;
                    case OperandType.InlineSwitch:
                        int length = BitConverter.ToInt32(bytes, position); position += 4;
                        int end = position + 4 * length; targets = new int[length];
                        for (int i = 0; i < length; i++) { targets[i] = end + BitConverter.ToInt32(bytes, position); position += 4; }
                        break;
                    default: throw new NotSupportedException("Unaudited IL operand kind: " + op.OperandType);
                }
                code.Add(new CodeInstruction(op, operand)); branches.Add(targets);
            }
            ILGenerator generator = new DynamicMethod("GamePerformanceGuardLabels", typeof(void), Type.EmptyTypes).GetILGenerator();
            Dictionary<int, Label> labels = new Dictionary<int, Label>();
            for (int i = 0; i < code.Count; i++)
            {
                if (branches[i] == null) continue;
                Label[] targets = new Label[branches[i].Length];
                for (int j = 0; j < targets.Length; j++)
                {
                    int destination = branches[i][j]; Label label;
                    if (!labels.TryGetValue(destination, out label))
                    {
                        label = generator.DefineLabel(); labels.Add(destination, label);
                        code[offsets[destination]].labels.Add(label);
                    }
                    targets[j] = label;
                }
                code[i].operand = code[i].opcode.OperandType == OperandType.InlineSwitch ? (object)targets : targets[0];
            }
            foreach (ExceptionHandlingClause clause in original.GetMethodBody().ExceptionHandlingClauses)
            {
                code[offsets[clause.TryOffset]].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
                int end = clause.HandlerOffset + clause.HandlerLength;
                int endIndex = end == bytes.Length ? code.Count - 1 : offsets[end] - 1;
                code[endIndex].blocks.Add(new ExceptionBlock(ExceptionBlockType.EndExceptionBlock));
                ExceptionBlockType kind = clause.Flags == ExceptionHandlingClauseOptions.Finally ? ExceptionBlockType.BeginFinallyBlock :
                    clause.Flags == ExceptionHandlingClauseOptions.Fault ? ExceptionBlockType.BeginFaultBlock : ExceptionBlockType.BeginCatchBlock;
                Type catchType = clause.Flags == ExceptionHandlingClauseOptions.Clause ? clause.CatchType : null;
                code[offsets[clause.HandlerOffset]].blocks.Add(new ExceptionBlock(kind, catchType));
                if (clause.Flags == ExceptionHandlingClauseOptions.Filter)
                    code[offsets[clause.FilterOffset]].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptFilterBlock));
            }
            return code;
        }
    }
}
