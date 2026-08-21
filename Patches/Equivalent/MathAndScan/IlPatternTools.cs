using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace PeakSafeOptimizer.Patches.Equivalent.MathAndScan
{
    internal static class IlPatternTools
    {
        internal static bool IsCall(CodeInstruction instruction, OpCode opcode, MethodInfo method)
        {
            return instruction.opcode == opcode && Equals(instruction.operand, method);
        }

        internal static bool IsField(CodeInstruction instruction, OpCode opcode, FieldInfo field)
        {
            return instruction.opcode == opcode && Equals(instruction.operand, field);
        }

        internal static bool IsLoadArgumentZero(CodeInstruction instruction)
        {
            return instruction.opcode == OpCodes.Ldarg_0 ||
                ((instruction.opcode == OpCodes.Ldarg || instruction.opcode == OpCodes.Ldarg_S) &&
                 GetNumericOperand(instruction.operand) == 0);
        }

        internal static bool IsLoadLocal(CodeInstruction instruction, int index)
        {
            if (index == 0 && instruction.opcode == OpCodes.Ldloc_0) return true;
            if (index == 1 && instruction.opcode == OpCodes.Ldloc_1) return true;
            if (index == 2 && instruction.opcode == OpCodes.Ldloc_2) return true;
            if (index == 3 && instruction.opcode == OpCodes.Ldloc_3) return true;
            return (instruction.opcode == OpCodes.Ldloc || instruction.opcode == OpCodes.Ldloc_S) &&
                GetLocalIndex(instruction.operand) == index;
        }

        internal static bool IsStoreLocal(CodeInstruction instruction, int index)
        {
            if (index == 0 && instruction.opcode == OpCodes.Stloc_0) return true;
            if (index == 1 && instruction.opcode == OpCodes.Stloc_1) return true;
            if (index == 2 && instruction.opcode == OpCodes.Stloc_2) return true;
            if (index == 3 && instruction.opcode == OpCodes.Stloc_3) return true;
            return (instruction.opcode == OpCodes.Stloc || instruction.opcode == OpCodes.Stloc_S) &&
                GetLocalIndex(instruction.operand) == index;
        }

        internal static int FindLabelTarget(IReadOnlyList<CodeInstruction> instructions, object operand)
        {
            if (operand is CodeInstruction targetInstruction)
            {
                for (int i = 0; i < instructions.Count; i++)
                    if (ReferenceEquals(instructions[i], targetInstruction)) return i;
                return -1;
            }
            for (int i = 0; i < instructions.Count; i++)
            {
                IList labels = GetMetadataList(instructions[i], "labels");
                for (int j = 0; j < labels.Count; j++)
                    if (Equals(labels[j], operand)) return i;
            }
            return -1;
        }

        internal static bool BranchTargets(
            IReadOnlyList<CodeInstruction> instructions,
            int branchIndex,
            int targetIndex)
        {
            return branchIndex >= 0 && branchIndex < instructions.Count &&
                targetIndex >= 0 && targetIndex < instructions.Count &&
                FindLabelTarget(instructions, instructions[branchIndex].operand) == targetIndex;
        }

        internal static void ValidateRemovalSafety(
            IReadOnlyList<CodeInstruction> instructions,
            int start,
            int endExclusive)
        {
            if (start < 0 || endExclusive > instructions.Count || start >= endExclusive)
                throw new InvalidOperationException("Invalid IL removal range");

            for (int i = start; i < endExclusive; i++)
            {
                if (GetMetadataList(instructions[i], "blocks").Count != 0)
                    throw new InvalidOperationException("Matched IL block intersects exception metadata");
                if (i == start)
                    continue;
                IList labels = GetMetadataList(instructions[i], "labels");
                for (int labelIndex = 0; labelIndex < labels.Count; labelIndex++)
                {
                    object label = labels[labelIndex];
                    for (int j = 0; j < instructions.Count; j++)
                    {
                        if ((j < start || j >= endExclusive) && ReferencesLabel(instructions[j].operand, label))
                            throw new InvalidOperationException("External branch enters the middle of matched IL block");
                    }
                }
            }
        }

        internal static void CopyMetadata(CodeInstruction source, CodeInstruction destination)
        {
            CopyMetadataList(source, destination, "labels");
            CopyMetadataList(source, destination, "blocks");
        }

        private static void CopyMetadataList(
            CodeInstruction source,
            CodeInstruction destination,
            string fieldName)
        {
            IList sourceList = GetMetadataList(source, fieldName);
            IList destinationList = GetMetadataList(destination, fieldName);
            for (int i = 0; i < sourceList.Count; i++) destinationList.Add(sourceList[i]);
        }

        private static IList GetMetadataList(CodeInstruction instruction, string fieldName)
        {
            FieldInfo field = typeof(CodeInstruction).GetField(
                fieldName, BindingFlags.Instance | BindingFlags.Public);
            if (field == null || !(field.GetValue(instruction) is IList list))
                throw new InvalidOperationException($"Harmony CodeInstruction.{fieldName} metadata is unavailable");
            return list;
        }

        internal static List<CodeInstruction> GetOriginalInstructions(MethodBase target)
        {
            MethodInfo reader = null;
            MethodInfo[] methods = typeof(PatchProcessor).GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo candidate = methods[i];
                ParameterInfo[] parameters = candidate.GetParameters();
                if (candidate.Name == "GetOriginalInstructions" && parameters.Length == 2 &&
                    !parameters[1].ParameterType.IsByRef)
                {
                    if (reader != null)
                        throw new InvalidOperationException("Harmony original-instruction reader was not unique");
                    reader = candidate;
                }
            }
            if (reader == null)
                throw new MissingMethodException(typeof(PatchProcessor).FullName, "GetOriginalInstructions");
            object result = reader.Invoke(null, new object[] { target, null });
            if (!(result is List<CodeInstruction> instructions))
                throw new InvalidOperationException("Harmony original-instruction reader returned an unexpected type");
            return instructions;
        }

        private static bool ReferencesLabel(object operand, object label)
        {
            if (Equals(operand, label))
                return true;
            if (operand is Array multiple)
            {
                for (int i = 0; i < multiple.Length; i++)
                    if (Equals(multiple.GetValue(i), label)) return true;
            }
            return false;
        }

        private static int GetLocalIndex(object operand)
        {
            if (operand is LocalBuilder builder) return builder.LocalIndex;
            if (operand is LocalVariableInfo variable) return variable.LocalIndex;
            return GetNumericOperand(operand);
        }

        private static int GetNumericOperand(object operand)
        {
            if (operand is int intValue) return intValue;
            if (operand is byte byteValue) return byteValue;
            if (operand is sbyte signedByteValue) return signedByteValue;
            if (operand is short shortValue) return shortValue;
            if (operand is ushort ushortValue) return ushortValue;
            return -1;
        }
    }
}
